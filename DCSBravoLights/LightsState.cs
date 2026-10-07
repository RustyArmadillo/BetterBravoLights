using System.Collections.Generic;
using BravoLights.Common;

namespace DCSBravoLights
{
    public class LightsState : ViewModelBase, ILightsState
    {
        public IEnumerable<string> LitLights
        {
            get { return litLights; }
        }

        private readonly HashSet<string> litLights = new();

        public void SetLight(string lightName, bool lit)
        {
            // Ignore empty or whitespace light names - they are invalid and cause KeyNotFoundException downstream.
            if (string.IsNullOrWhiteSpace(lightName))
            {
                return;
            }

            bool changed;
            if (lit)
            {
                changed = litLights.Add(lightName);
            }
            else
            {
                changed = litLights.Remove(lightName);
            }

            if (changed)
            {
                RaisePropertyChanged(lightName);
            }
        }
    }
}
