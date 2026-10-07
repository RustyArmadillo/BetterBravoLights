using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using BravoLights.Common;

namespace BravoLights.UI
{
    /// <summary>
    /// Interaction logic for LightsWindow.xaml
    /// </summary>
    public partial class LightsWindow : Window
    {
        public LightsWindow()
        {
            InitializeComponent();
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            Title = $"{ProgramInfo.ProductNameAndVersion} - Lights Monitor";
        }

        private MainViewModel viewModel;
        private bool updatingTestChecks;

        public GlobalLightController LightController { get; set; }

        public MainViewModel ViewModel
        {
            get
            {
                return (MainViewModel)DataContext;
            }
            set
            {
                viewModel = value;
                viewModel.PropertyChanged += ViewModel_PropertyChanged;
                DataContext = new CombinedDataContext
                {
                    MainState = value,
                    ExpressionAndVariablesViewModel = eavVM
                };
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "LightExpressions")
            {
                UpdateMonitor();
            }
        }

        private string monitoredLight = "";

        private void LightTestCheckBoxChanged(object sender, RoutedEventArgs e)
        {
            if (updatingTestChecks) return;

            var checkBox = e.OriginalSource as CheckBox;
            if (checkBox?.Tag is not string lightName) return;

            if (TestModeToggle.IsChecked == true)
            {
                monitoredLight = lightName;
                UpdateMonitor();
                if (viewModel == null) return;
                viewModel.SetTestLight(lightName, checkBox.IsChecked == true);
            }
            else
            {
                // Outside test mode, one checked box identifies the light being inspected.
                monitoredLight = checkBox.IsChecked == true ? lightName : null;
                SetMonitorCheckBoxOnly(monitoredLight);
                UpdateMonitor();
            }
        }

        private void TestMode_Changed(object sender, RoutedEventArgs e)
        {
            if (viewModel == null) return;

            var enabled = TestModeToggle.IsChecked == true;
            viewModel.TestMode = enabled;
            if (enabled)
            {
                foreach (var checkBox in GetTestLightCheckBoxes(MonitorGrid))
                {
                    if (checkBox.IsChecked == true && checkBox.Tag is string lightName)
                    {
                        viewModel.SetTestLight(lightName, true);
                    }
                }
            }
            else
            {
                SetMonitorCheckBoxOnly(monitoredLight);
            }
            if (LightController != null)
            {
                LightController.TestMode = enabled;
            }
        }

        private void AllLightsOff_Click(object sender, RoutedEventArgs e)
        {
            viewModel?.SetAllTestLights(false);
            SetAllTestCheckBoxes(false);
        }

        private void AllLightsOn_Click(object sender, RoutedEventArgs e)
        {
            viewModel?.SetAllTestLights(true);
            SetAllTestCheckBoxes(true);
        }

        private void SetAllTestCheckBoxes(bool isChecked)
        {
            updatingTestChecks = true;
            foreach (var checkBox in GetTestLightCheckBoxes(MonitorGrid))
            {
                checkBox.IsChecked = isChecked;
            }
            updatingTestChecks = false;
        }

        private void SetMonitorCheckBoxOnly(string lightName)
        {
            updatingTestChecks = true;
            foreach (var checkBox in GetTestLightCheckBoxes(MonitorGrid))
            {
                checkBox.IsChecked = string.Equals(checkBox.Tag as string, lightName, StringComparison.OrdinalIgnoreCase);
            }
            updatingTestChecks = false;
        }

        private static System.Collections.Generic.IEnumerable<CheckBox> GetTestLightCheckBoxes(DependencyObject parent)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is CheckBox checkBox && checkBox.Tag is string)
                {
                    yield return checkBox;
                }
                foreach (var nested in GetTestLightCheckBoxes(child))
                {
                    yield return nested;
                }
            }
        }

        private readonly ExpressionAndVariablesViewModel eavVM = new();

        private void UpdateMonitor()
        {
            LightExpression lightExpression = null;

            if (monitoredLight != null && viewModel != null)
            {
                viewModel.LightExpressions.TryGetValue(monitoredLight, out lightExpression);
            }
            eavVM.Monitor(lightExpression);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // Test output should never remain active after the Monitor is hidden.
            TestModeToggle.IsChecked = false;

            // Hide instead of close
            e.Cancel = true;
            Hide();

            // Unsubscribe whilst invisible
            UpdateMonitor();
        }
    }

    class CombinedDataContext : ViewModelBase
    {
        private MainViewModel mainState;
        public MainViewModel MainState
        {
            get { return mainState; }
            set { mainState = value; }
        }

        private ExpressionAndVariablesViewModel eavVM;

        public ExpressionAndVariablesViewModel ExpressionAndVariablesViewModel
        {
            get { return eavVM; }
            set { eavVM = value; }
        }

        private int textSize = 12;
        public int TextSize
        {
            get { return textSize; }
            set { SetProperty(ref textSize, value); }
        }
    }

    public class VariableState : ViewModelBase
    {
        public VariableState()
        {
            ValueText = "No value received yet";
            IsError = true;
        }

        public string Name { get; set; }

        private string val;
        public string ValueText
        {
            get { return val; }
            private set
            {
                SetProperty(ref val, value);
            }
        }

        private bool isError;
        public bool IsError
        {
            get { return isError; }
            private set
            {
                SetProperty(ref isError, value);
            }
        }

        private object valueObject;
        public object Value
        {
            get { return valueObject; }
            set
            {
                SetProperty(ref valueObject, value);

                var exception = value as Exception;
             
                IsError = exception != null;
                
                if (exception != null)
                {
                    ValueText = exception.Message;
                }
                else
                {
                    ValueText = value.ToString();
                }
            }
        }
    }
}
