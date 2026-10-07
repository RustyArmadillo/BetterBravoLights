using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace BravoLights.Installation
{
    static class FlightSimulatorPaths
    {
        /// <summary>
        /// Gets the location of the main Flight Simulator 2024 installation.
        /// </summary>
        public static string FlightSimulatorPath
        {
            get
            {
                var localAppData = (UnitTestRoot == null) ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : Path.Join(UnitTestRoot, "LOCALAPPDATA");
                var appData = (UnitTestRoot == null) ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : Path.Join(UnitTestRoot, "APPDATA");
                // Prefer 2024 when both simulator versions are installed. Their user-data
                // locations use different package IDs/folder names from MSFS 2020.
                var pathsToTry = new[]
                {
                    Path.Join(localAppData, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache"),
                    Path.Join(appData, "Microsoft Flight Simulator 2024"),
                    Path.Join(localAppData, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache"),
                    Path.Join(appData, "Microsoft Flight Simulator")
                };

                foreach (var path in pathsToTry)
                {
                    if (File.Exists(Path.Join(path, "FlightSimulator2024.CFG")) || File.Exists(Path.Join(path, "UserCfg.opt")))
                    {
                        return path;
                    }
                }

                var pathsTried = String.Join(", ", pathsToTry);
                throw new Exception($"Could not locate main Flight Simulator 2024 path. Paths tried: {pathsTried}");
            }
        }

        /// <summary>
        /// Gets the path that the BBL .exe is running from.
        /// </summary>
        public static string BetterBravoLightsPath
        {
            get
            {
                if (UnitTestRoot == null)
                {
                    return Application.StartupPath;
                }

                return Path.Join(Application.StartupPath, "..", "..", "..", "..", "BravoLights", "bin", "Debug", "net5.0-windows");
            }
        }

        /// <summary>
        /// Gets the location of the MSFS exe.xml file, which may not actually exist yet.
        /// </summary>
        public static string ExeXmlPath
        {
            get
            {
                return Path.Join(FlightSimulatorPath, "exe.xml");
            }
        }

        /// <summary>
        /// Gets the location of the UserCfg.opt file.
        /// </summary>
        private static string UserCfgOptPath
        {
            get
            {
                return Path.Join(FlightSimulatorPath, "UserCfg.opt");
            }
        }

        private static readonly Regex installedPackagesPathRegex = new("^InstalledPackagesPath \"(.*)\"");

        /// <summary>
        /// Gets the location of the Official and Community directories.
        /// </summary>
        public static string MSFSPackagesPath
        {
            get
            {
                var lines = File.ReadAllLines(UserCfgOptPath);
                foreach (var line in lines)
                {
                    var match = installedPackagesPathRegex.Match(line);
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }

                throw new Exception("Cannot locate FS packages path");
            }
        }


        /// <summary>
        /// Gets the location of the Community directory.
        /// </summary>
        public static string CommunityPath
        {
            get { return Path.Join(MSFSPackagesPath, "Community"); }
        }


        private const string WasmModuleName = "better-bravo-lights-lvar-module";

        public static string InstalledWasmModulePath
        {
            get { return Path.Join(CommunityPath, WasmModuleName); }
        }
        public static string IncludedWasmModulePath
        {
            get { return Path.Join(BetterBravoLightsPath, "Packages", WasmModuleName); }
        }

        public static string BuiltInConfigIniPath
        {
            get { return Path.Join(BetterBravoLightsPath, "Config.BuiltIn.ini"); }
        }

        public static string UserRuntimePath
        {
            get
            {
                return Path.Combine(new DirectoryInfo(BetterBravoLightsPath).Parent.FullName);
            }
        }
        public static string UserConfigIniPath
        {
            get
            {
                return Path.Combine(UserRuntimePath, "Config.ini");
            }
        }

        internal static string UnitTestRoot = null;
    }
}
