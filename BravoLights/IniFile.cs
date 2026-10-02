using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace BravoLights
{
    class IniFile
    {
        private Dictionary<string, IniSection> sections = new();

        private static readonly Regex sectionRegex = new("\\[(.*)\\]");
        private static readonly Regex keyValueRegex = new("^(.*?)\\s*([+|&]?=)\\s*(.*)$");

        public void LoadConfigFromFile(string filename)
        {
            try
            {
                var lines = File.ReadAllLines(filename);
                LoadConfigLines(lines);
            }
            catch
            {
                throw new Exception($"Failed to read {filename}");
            }
        }

        public void LoadConfigLines(string[] configLines)
        {
            ICollection<IniSection> newSections = new List<IniSection>();

            var sections = new Dictionary<string, IniSection>();
            IniSection defaultSection = null;

            foreach (var rawLine in configLines)
            {
                var line = rawLine.Trim();

                if (line.StartsWith(";"))
                {
                    // Comment
                    continue;
                }

                if (line.Length == 0)
                {
                    // Empty line
                    continue;
                }

                var sectionMatch = sectionRegex.Match(line);
                if (sectionMatch.Success)
                {
                    var sectionNamesString = sectionMatch.Groups[1].Value;
                    var sectionNames = sectionNamesString.Split(',');
                    newSections.Clear();

                    foreach (var sectionName in sectionNames)
                    {
                        var trimmedSectionName = sectionName.Trim();
                        if (!sections.TryGetValue(trimmedSectionName, out IniSection section))
                        {
                            section = new IniSection();
                            sections[trimmedSectionName] = section;
                            if (trimmedSectionName == "Default")
                                defaultSection = section;
                        }
                        newSections.Add(section);
                    }
                    continue;
                }


                var keyValueMatch = keyValueRegex.Match(line);
                if (keyValueMatch.Success)
                {
                    var key = keyValueMatch.Groups[1].Value;
                    var op = keyValueMatch.Groups[2].Value;
                    var value = keyValueMatch.Groups[3].Value;
                    foreach (var section in newSections)
                    {
                        if (op != "=" &&
                            defaultSection != null && section != defaultSection &&
                            defaultSection.TryGetValue(key, out string defaultValue))
                        {
                            switch(op)
                            {
                                case "&=":
                                    section.Set(key, $"{defaultValue} AND {value}");
                                    break;
                                case "|=":
                                case "+=":
                                    section.Set(key, $"{defaultValue} OR {value}");
                                    break;
                            }
                        }
                        else
                        {
                            section.Set(key, value);
                        }
                    }
                }
            }

            this.sections = sections;
        }

        public bool HasSection(string sectionName)
        {
            if (sections.TryGetValue(sectionName, out var section))
            {
                return !section.IsEmpty;
            }
            return false;
        }

        public string GetValueOrNull(string sectionName, string key)
        {
            if (sections.TryGetValue(sectionName, out var section))
            {
                if (section.TryGetValue(key, out var value))
                {
                    return value;
                }
            }

            return null;
        }
    }

    class IniSection
    {
        private readonly Dictionary<string, string> storage = new();

        public IniSection()
        {
        }

        public bool IsEmpty
        {
            get { return storage.Count == 0; }
        }

        public void Set(string key, string value)
        {
            storage[key] = value;
        }

        public bool TryGetValue(string key, out string value)
        {
            return storage.TryGetValue(key, out value);
        }
    }
}
