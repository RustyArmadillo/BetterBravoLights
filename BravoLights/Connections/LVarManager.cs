using System;
using System.Collections.Generic;
using System.Linq;
using BravoLights.Ast;
using BravoLights.Common;

namespace BravoLights.Connections
{
    public class LVarManager : IConnection
    {
        public readonly static LVarManager Connection = new();

        private readonly Dictionary<string, double> lvarValues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, EventHandler<ValueChangedEventArgs>> handlers = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> knownLVars = new(StringComparer.OrdinalIgnoreCase);
        private ILVarChannel lvarChannel;

        public void AddListener(IVariable variable, EventHandler<ValueChangedEventArgs> handler)
        {
            var name = ((LvarExpression)variable).LVarName;
            lock (this)
            {
                handlers.TryGetValue(name, out var existing);
                handlers[name] = (EventHandler<ValueChangedEventArgs>)Delegate.Combine(existing, handler);
                if (existing == null)
                {
                    if (knownLVars.Add(name)) OnLVarListChanged?.Invoke(this, EventArgs.Empty);
                    lvarChannel?.Subscribe(name);
                }
                SendLastValue(name, handler);
            }
        }

        public void RemoveListener(IVariable variable, EventHandler<ValueChangedEventArgs> handler)
        {
            var name = ((LvarExpression)variable).LVarName;
            lock (this)
            {
                handlers.TryGetValue(name, out var existing);
                var remaining = (EventHandler<ValueChangedEventArgs>)Delegate.Remove(existing, handler);
                if (remaining == null)
                {
                    handlers.Remove(name);
                    lvarChannel?.Unsubscribe(name);
                }
                else
                {
                    handlers[name] = remaining;
                }
            }
        }

        public void SetLVarChannel(ILVarChannel channel)
        {
            lock (this)
            {
                lvarChannel = channel;
            }
        }

        public void RegisterCurrentLVars()
        {
            lock (this)
            {
                foreach (var name in handlers.Keys)
                {
                    lvarChannel?.Subscribe(name);
                }
            }
        }

        public void UpdateLVarValue(string name, double value)
        {
            lock (this)
            {
                lvarValues[name] = value;
                if (handlers.TryGetValue(name, out var handler))
                {
                    handler(this, new ValueChangedEventArgs { NewValue = value });
                }
            }
        }

        private void SendLastValue(string name, EventHandler<ValueChangedEventArgs> handler)
        {
            if (lvarChannel == null || lvarChannel.SimState == SimState.SimExited)
            {
                VariableHandlerUtils.SendNoConnectionError(this, handler);
            }
            else if (lvarValues.TryGetValue(name, out var value))
            {
                handler(this, new ValueChangedEventArgs { NewValue = value });
            }
            else
            {
                VariableHandlerUtils.SendNoValueError(this, handler);
            }
        }

        public IList<string> LVarList
        {
            get { lock (this) return knownLVars.ToList(); }
        }

        public event EventHandler OnLVarListChanged;
    }
}
