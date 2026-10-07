namespace BravoLights.Connections
{
    /// <summary>Provides direct SimConnect access to individual LVars.</summary>
    public interface ILVarChannel
    {
        SimState SimState { get; }
        void Subscribe(string name);
        void Unsubscribe(string name);
    }
}
