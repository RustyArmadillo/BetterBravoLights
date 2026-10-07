using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Threading;
using BravoLights.Ast;
using BravoLights.Common;
using Microsoft.FlightSimulator.SimConnect;
using NLog;

namespace BravoLights.Connections
{
    enum DefineId : uint { DynamicStart = 100 }

    enum RequestId : uint
    {
        SimState = 1,
        AircraftLoaded = 2,
        FlightLoaded = 3,

        // The start of dynamically-allocated request ids for simulator variables
        DynamicStart = DefineId.DynamicStart
    }

    class SimConnectConnection : IConnection, ILVarChannel
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public static SimConnectConnection Connection = new();

        private Timer reconnectTimer = null;
        private Timer detectExitTimer = null;

        // MSFS does use SimConnect to tell us when it's exited, but some users have reported that
        // BBL sometimes doesn't exit. So as well as exiting when the sim tells us it's exited,
        // we'll also exit if we haven't heard anything from the sim for a while. We'll also
        // prod the sim every so often, to ensure that we _should_ hear something.
        private DateTime lastReceivedResponseFromSim = DateTime.MinValue;
        private static readonly TimeSpan ExitWhenNotHeardFromSimFor = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan SimLivenessPollPeriod = ExitWhenNotHeardFromSimFor / 2;

        private SimState simState = SimState.SimExited;

        private SimConnectConnection()
        {
            LVarManager.Connection.SetLVarChannel(this);
        }

        public void Start()
        {
            logger.Debug("Start");

            ConnectNow();
        }

        private uint nextVariableId = (uint)DefineId.DynamicStart;

        private readonly Dictionary<uint, NameAndUnits> idToName = new();
        private readonly Dictionary<NameAndUnits, uint> nameToId = new(new NameAndUnitsComparer());
        private readonly Dictionary<NameAndUnits, EventHandler<ValueChangedEventArgs>> variableHandlers =
            new(new NameAndUnitsComparer());
        private readonly Dictionary<NameAndUnits, double> lastReportedValue = new(new NameAndUnitsComparer());
        private readonly Dictionary<uint, string> lvarIdToName = new();
        private readonly Dictionary<string, uint> lvarNameToId = new(StringComparer.OrdinalIgnoreCase);

        private void SubscribeToSimConnect(NameAndUnits nameAndUnits)
        {
            nameToId.TryGetValue(nameAndUnits, out var id);

            // Do we already have an id for this variable?
            if (id == 0)
            {
                // No. Create one.
                id = ++nextVariableId;

                idToName[id] = nameAndUnits;
                nameToId[nameAndUnits] = id;
            }

            simconnect.AddToDataDefinition((DefineId)id, nameAndUnits.Name, nameAndUnits.Units, SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
            simconnect.RegisterDataDefineStruct<double>((DefineId)id);            
            simconnect.RequestDataOnSimObject((RequestId)id, (DefineId)id, 0, SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
        }

        private void UnsubscribeFromSimConnect(SimVarExpression simvar)
        {
            var name = simvar.NameAndUnits;

            var id = nameToId[name];

            logger.Debug("UnsubscribeFromSimConnect:{0} ({1})", name, id);

            // We're unsubscribing from this variable, so any value is going to be out of date, so remove the old value
            lastReportedValue.Remove(name);

            simconnect.RequestDataOnSimObject((RequestId)id, (DefineId)id, 0, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            simconnect.ClearDataDefinition((DefineId)id);
        }

        public void AddListener(IVariable variable, EventHandler<ValueChangedEventArgs> handler)
        {
            var simvar = (SimVarExpression)variable;
            var nau = simvar.NameAndUnits;

            logger.Debug("AddListener {0}. Existing: {1}", variable.Identifier, variableHandlers.Count);

            lock (this)
            {
                variableHandlers.TryGetValue(nau, out var existingHandlers);
                var newHandlers = (EventHandler<ValueChangedEventArgs>)Delegate.Combine(existingHandlers, handler);
                variableHandlers[nau] = newHandlers;

                if (existingHandlers == null)
                {
                    if (this.simconnect != null)
                    {
                        SubscribeToSimConnect(nau);
                    }
                    else if (reconnectTimer == null)
                    {
                        ConnectNow();
                    }
                }

                SendLastValue(nau, handler);
            }
        }

        private void SendLastValue(NameAndUnits variable, EventHandler<ValueChangedEventArgs> handler)
        {
            if (simState == SimState.SimRunning)
            {
                if (lastReportedValue.TryGetValue(variable, out double lastValue))
                {
                    handler(this, new ValueChangedEventArgs { NewValue = lastValue });
                }
                else
                {
                    VariableHandlerUtils.SendNoValueError(this, handler);
                }
            }
            else
            {
                VariableHandlerUtils.SendNoConnectionError(this, handler);
            }
        }

        public void RemoveListener(IVariable variable, EventHandler<ValueChangedEventArgs> handler)
        {
            var simvar = (SimVarExpression)variable;
            var nau = simvar.NameAndUnits;

            logger.Debug("RemoveListener {0}", variable.Identifier);

            lock (this)
            {
                variableHandlers.TryGetValue(nau, out var existingHandlers);
                var newListeners = (EventHandler<ValueChangedEventArgs>)Delegate.Remove(existingHandlers, handler);

                if (newListeners == null)
                {
                    variableHandlers.Remove(nau);

                    if (this.simconnect != null)
                    {
                        UnsubscribeFromSimConnect(simvar);
                    }
                }
                else
                {
                    variableHandlers[nau] = newListeners;
                }
            }
        }

        private const int WM_USER_SIMCONNECT = 0x0402;

        private SimConnect simconnect;

        public void ReceiveMessage()
        {
            if (simconnect != null)
            {
                try
                {
                    simconnect.ReceiveMessage();
                }
                catch (Exception)
                {
                    // Just squash. Sim's probably about to exit.
                }
            }
        }

        private void ReconnectTimerElapsed(object sender)
        {
            ConnectNow();
        }

        private void ConnectNow()
        {
            logger.Debug("Attempting to connect to simulator");

            if (reconnectTimer != null)
            {
                reconnectTimer.Dispose();
                reconnectTimer = null;
            }

            if (simconnect != null)
            {
                logger.Debug("Already connected");
                return;
            }

            try
            {
                simconnect = new SimConnect("BravoLights", HWnd, WM_USER_SIMCONNECT, null, 0);
                logger.Debug("SimConnect connected to simulator successfully");

                simconnect.OnRecvSimobjectData += Simconnect_OnRecvSimobjectData;
                simconnect.OnRecvQuit += Simconnect_OnRecvQuit;

                simconnect.OnRecvEvent += Simconnect_OnRecvEvent;
                simconnect.SubscribeToSystemEvent(RequestId.SimState, "Sim");
                simconnect.SubscribeToSystemEvent(RequestId.AircraftLoaded, "AircraftLoaded");
                simconnect.SubscribeToSystemEvent(RequestId.FlightLoaded, "FlightLoaded");

                simconnect.OnRecvSystemState += Simconnect_OnRecvSystemState;

                RequestAircraftAndFlightStatus();

                RegisterCurrentVariables();
                LVarManager.Connection.RegisterCurrentLVars();
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Connection failure");

                reconnectTimer = new(ReconnectTimerElapsed, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);

                if (simState == SimState.SimRunning)
                {
                    RaiseSimStateChanged(SimState.SimStopped);
                }
            }
        }

        private void RequestAircraftAndFlightStatus()
        {
            simconnect.RequestSystemState(RequestId.AircraftLoaded, "AircraftLoaded");
            simconnect.RequestSystemState(RequestId.FlightLoaded, "FlightLoaded");
        }

        private void Simconnect_OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
        {
            lock (this)
            {
                logger.Debug("SimConnectOnRecvEvent {0}", data.uEventID);
                lastReceivedResponseFromSim = DateTime.UtcNow;

                switch ((RequestId)data.uEventID)
                {
                    case RequestId.SimState:
                        logger.Trace("SimState {0}", data.dwData);
                        RaiseSimStateChanged(data.dwData == 1 ? SimState.SimRunning : SimState.SimStopped);
                        break;
                    case RequestId.AircraftLoaded:
                        {
                            var fileData = (SIMCONNECT_RECV_EVENT_FILENAME)data;
                            HandleAircraftChanged(fileData.szFileName);
                        }
                        break;
                    case RequestId.FlightLoaded:
                        {
                            var fileData = (SIMCONNECT_RECV_EVENT_FILENAME)data;
                            HandleFlightLoaded(fileData.szFileName);
                        }
                        break;
                }
            }
        }

        private void Simconnect_OnRecvSystemState(SimConnect sender, SIMCONNECT_RECV_SYSTEM_STATE data)
        {
            lock (this)
            {
                logger.Debug("SimConnectOnRecvSystemState {0}", data.dwRequestID);
                lastReceivedResponseFromSim = DateTime.UtcNow;

                switch ((RequestId)data.dwRequestID)
                {
                    case RequestId.SimState:
                        RaiseSimStateChanged(data.dwInteger == 1 ? SimState.SimRunning : SimState.SimStopped);
                        break;
                    case RequestId.AircraftLoaded:
                        HandleAircraftChanged(data.szString);
                        break;
                    case RequestId.FlightLoaded:
                        HandleFlightLoaded(data.szString);
                        break;
                }
            }
        }

        private void RaiseSimStateChanged(SimState state)
        {
            lock (this)
            {
                if (simState == state)
                {
                    // State is unchanged
                    return;
                }

                logger.Debug("SimStateChange {0}", state);

                simState = state;

                if (simState == SimState.SimRunning)
                {
                    if (detectExitTimer == null)
                    {
                        // This is the first time we've connected to the sim. Start an interval timer which 
                        detectExitTimer = new(ExitDetectionTimerElapsed, null, TimeSpan.Zero, SimLivenessPollPeriod);

                    }
                }

                if (simState == SimState.SimExited)
                {
                    // The sim has exited. We're likely to be exiting soon, but in
                    // case we don't, we should tidy up.

                    // Any previous values and subscriptions/definitions are now not valid.
                    idToName.Clear();
                    nameToId.Clear();
                    lastReportedValue.Clear();

                    lvarIdToName.Clear();
                    lvarNameToId.Clear();

                    // SimConnect has gone away; presumably MSFS has exited.
                    // BetterBravoLights itself might not be exiting (if it's being run without install),
                    // but we need to clean up these registrations.
                    this.idToName.Clear();
                    this.nameToId.Clear();
                    simconnect = null;

                    // Update all existing subscribers
                    foreach (var kvp in variableHandlers)
                    {
                        var variable = kvp.Key;
                        var handlers = kvp.Value;

                        SendLastValue(variable, handlers);
                    }
                }
                else
                {
                    // Despite subscribing to AircraftLoaded and FlightLoaded events, we don't actually seem
                    // to receive them, so we explicitly ask for them when we get SimStart/SimStop events.
                    RequestAircraftAndFlightStatus();
                }

                OnSimStateChanged?.Invoke(this, new SimStateEventArgs { SimState = state });
            }
        }

        private static readonly Regex AircraftPathRegex = new("Airplanes\\\\(.*)\\\\");

        private void HandleAircraftChanged(string aircraftPath)
        {
            logger.Debug("AircraftChanged {0}", aircraftPath);

            if (OnAircraftLoaded != null)
            {
                // aircraftPath is something like
                // SimObjects\\Airplanes\\Asobo_C172SP_Classic\\aircraft.CFG
                // We just want 'Asobo_C172SP_Classic'
                var match = AircraftPathRegex.Match(aircraftPath);
                if (match.Success)
                {
                    var aircraftName = match.Groups[1].Value;
                    Debug.WriteLine($"HandleAircraftChanged {aircraftName}. Checking LVars");

                    // Note: LVars are not registered by an aircraft until a little while _after_ it has loaded.
                    // So the first time we get an AircraftChanged event, the lvars will not be present.
                    // However, we request aircraft + flight information on each SimStart/SimStop, which should catch them.
                    OnAircraftLoaded(this, new AircraftEventArgs { Aircraft = aircraftName });
                }
            }
        }

        private void HandleFlightLoaded(string flightPath)
        {
            // Examples:
            // flights\other\MainMenu.FLT
            // C:\Users\royston\AppData\Local\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\LocalState\MISSIONS\ACTIVITIES\ASOBO-BUSHTRIP-FINLAND_SAVE\ASOBO-BUSHTRIP-FINLAND_SAVE\ASOBO-BUSHTRIP-FINLAND_SAVE.FLT
            // C:\Users\royston\AppData\Local\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\LocalState\MISSIONS\ACTIVITIES\ASOBO-BUSHTRIP-FINLAND_SAVE\AUTOSAVE\\BCKP\\ASOBO-BUSHTRIP-FINLAND_SAVE.FLT
            // missions\Asobo\Tutorials\VFRNavigation\LandmarkNavigation\03_Training_LandmarkNavigation.FLT

            logger.Debug("FlightLoaded {0}", flightPath);

            InMainMenu = flightPath.EndsWith("flights\\other\\mainmenu.flt", StringComparison.InvariantCultureIgnoreCase);
            Debug.WriteLine($"HandleFlightLoaded. {flightPath}. Checking LVars");
        }

        public event EventHandler OnInMainMenuChanged;

        private bool inMainMenu = true;
        public bool InMainMenu
        {
            get
            {
                return inMainMenu;
            }
            private set
            {
                if (inMainMenu == value)
                {
                    return;
                }

                inMainMenu = value;
                OnInMainMenuChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        // Called when MSFS quits
        private void Simconnect_OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            logger.Debug("SimConnectOnRecvQuit");

            RaiseSimStateChanged(SimState.SimExited);
        }

        private void RegisterCurrentVariables()
        {
            foreach (var nau in this.variableHandlers.Keys)
            {
                SubscribeToSimConnect(nau);
            }
        }

        private void Simconnect_OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            lock (this)
            {
                if (idToName.TryGetValue(data.dwRequestID, out NameAndUnits nau))
                {
                    var newValue = (double)data.dwData[0];

                    logger.Trace("RecvSimObjectData for {0} ({1}) = {2}", nau, data.dwRequestID, newValue);

                    variableHandlers.TryGetValue(nau, out var handlers);

                    if (handlers != null)
                    {
                        lastReportedValue[nau] = newValue;
                        var e = new ValueChangedEventArgs { NewValue = newValue };
                        handlers(this, e);
                    }
                }
                else if (lvarIdToName.TryGetValue(data.dwRequestID, out var lvarName))
                {
                    LVarManager.Connection.UpdateLVarValue(lvarName, (double)data.dwData[0]);
                }
                else
                {
                    logger.Trace("RecvSimObjectData for unexpected id {0}", data.dwRequestID);
                }
            }
        }

        public static IntPtr HWnd { get; set; }

        public event EventHandler<AircraftEventArgs> OnAircraftLoaded;
        public SimState SimState
        {
            get { return simState; }
        }
        public event EventHandler<SimStateEventArgs> OnSimStateChanged;

        void ILVarChannel.Subscribe(string name)
        {
            if (simconnect == null || lvarNameToId.ContainsKey(name)) return;
            try
            {
                var id = ++nextVariableId;
                lvarNameToId[name] = id;
                lvarIdToName[id] = name;
                simconnect.AddToDataDefinition((DefineId)id, $"L:{name}", "number", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
                simconnect.RegisterDataDefineStruct<double>((DefineId)id);
                simconnect.RequestDataOnSimObject((RequestId)id, (DefineId)id, 0, SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
            }
            catch (Exception ex)
            {
                lvarNameToId.Remove(name);
                lvarIdToName.Remove(nextVariableId);
                logger.Warn(ex, "Unable to subscribe to LVar {0}", name);
            }
        }

        void ILVarChannel.Unsubscribe(string name)
        {
            if (simconnect == null || !lvarNameToId.TryGetValue(name, out var id)) return;
            try
            {
                simconnect.RequestDataOnSimObject((RequestId)id, (DefineId)id, 0, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                simconnect.ClearDataDefinition((DefineId)id);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Unable to unsubscribe from LVar {0}", name);
            }
            lvarNameToId.Remove(name);
            lvarIdToName.Remove(id);
        }


        private void ExitDetectionTimerElapsed(object sender)
        {
            if (simconnect != null)
            {
                try
                {
                    simconnect.RequestSystemState(RequestId.SimState, "Sim");
                }
                catch (Exception)
                {
                    // Sim has probably exited, but wait for the timer to expire, to make sure
                    // it's not just a temporary glitch.
                }
            }

            if (lastReceivedResponseFromSim < DateTime.UtcNow.Subtract(ExitWhenNotHeardFromSimFor))
            {
                logger.Info("No response from the simulator for a while. Checking if it's still running.");

                // It's been a while since we received anything from the sim.
                // At the very least it's not responding.
                // If the FS .exe has also gone, it's time for us to exit.
                var fsRunning = Process.GetProcessesByName("FlightSimulator").Length > 0;

                if (fsRunning)
                {
                    logger.Info("Simulator is still running.");
                }
                else
                {
                    logger.Info("Simulator has exited (crashed?)");
                    detectExitTimer.Dispose();
                    RaiseSimStateChanged(SimState.SimExited);
                }
            }
        }
    }

    public enum SimState
    {
        SimRunning,
        SimStopped,
        SimExited
    }

    class NameAndUnits
    {
        public string Name;
        public string Units;

        public override string ToString()
        {
            return $"{Name}, {Units}";
        }
    }

    class NameAndUnitsComparer : IEqualityComparer<NameAndUnits>
    {
        public bool Equals(NameAndUnits x, NameAndUnits y)
        {
            return x.Name == y.Name && x.Units == y.Units;
        }

        public int GetHashCode([DisallowNull] NameAndUnits obj)
        {
            return obj.Name.GetHashCode();
        }
    }

    public class SimStateEventArgs : EventArgs
    {
        public SimState SimState;
    }

    class AircraftEventArgs : EventArgs
    {
        public string Aircraft;
    }
}
