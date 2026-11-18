using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace TestGate.Planning
{
    public sealed class TestOutcome
    {
        public string Key { get; set; }          // Class.Method (short class name)
        public bool Passed { get; set; }
        public DateTime RunUtc { get; set; }
        public string Source { get; set; }       // results file
    }

    /// <summary>
    /// Latest test outcomes from the files the GATE wrote (dotnet .trx, Unity NUnit .xml). Agents can't write Dashboard/Data/
    /// through the recorder's eyes without it being observed; the gate also logs each file's hash (TEST_RESULT events).
    /// </summary>
    public sealed class TestResultsIndex
    {
        private readonly Dictionary<string, List<TestOutcome>> _byKey = new Dictionary<string, List<TestOutcome>>(StringComparer.Ordinal);

        public DateTime? LatestRunUtc { get; private set; }

        public static TestResultsIndex Load(string root)
        {
            var idx = new TestResultsIndex();
            string dir = Path.Combine(root, "Dashboard", "Data");
            if (!Directory.Exists(dir))
                return idx;
            foreach (var trx in Directory.GetFiles(dir, "*.trx", SearchOption.AllDirectories))
                idx.ReadTrx(trx);
            foreach (var xml in Directory.GetFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                idx.ReadNUnit(xml);
            return idx;
        }

        public void Add(TestOutcome o)
        {
            if (!_byKey.TryGetValue(o.Key, out var list))
                _byKey[o.Key] = list = new List<TestOutcome>();
            list.Add(o);
            if (LatestRunUtc == null || o.RunUtc > LatestRunUtc)
                LatestRunUtc = o.RunUtc;
        }

        /// <summary>Outcomes for "Class.Method", or every test of the class for "Class.*" (parameterised cases included).</summary>
        public List<TestOutcome> Find(string key)
        {
            if (key.EndsWith(".*", StringComparison.Ordinal))
            {
                string prefix = key.Substring(0, key.Length - 1);
                return _byKey.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).SelectMany(kv => kv.Value).ToList();
            }
            return _byKey.TryGetValue(key, out var l) ? l : new List<TestOutcome>();
        }

        private void ReadTrx(string path)
        {
            try
            {
                XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                var doc = XDocument.Load(path);
                var classes = doc.Descendants(ns + "UnitTest").ToDictionary(
                    u => (string)u.Attribute("id"),
                    u => (string)u.Element(ns + "TestMethod")?.Attribute("className") ?? "");
                var when = File.GetLastWriteTimeUtc(path);
                foreach (var r in doc.Descendants(ns + "UnitTestResult"))
                {
                    string cls = classes.TryGetValue((string)r.Attribute("testId") ?? "", out var c) ? c : "";
                    string name = (string)r.Attribute("testName") ?? "";
                    Add(new TestOutcome { Key = Short(cls) + "." + Method(name), Passed = (string)r.Attribute("outcome") == "Passed", RunUtc = when, Source = path });
                }
            }
            catch (Exception)
            {
                // unreadable results = no evidence (the requirement shows NOT_RUN), never a crash
            }
        }

        private void ReadNUnit(string path)
        {
            try
            {
                var doc = XDocument.Load(path);
                var when = File.GetLastWriteTimeUtc(path);
                foreach (var tc in doc.Descendants("test-case"))
                {
                    string cls = (string)tc.Attribute("classname") ?? "";
                    string name = (string)tc.Attribute("methodname") ?? (string)tc.Attribute("name") ?? "";
                    Add(new TestOutcome { Key = Short(cls) + "." + Method(name), Passed = (string)tc.Attribute("result") == "Passed", RunUtc = when, Source = path });
                }
            }
            catch (Exception)
            {
            }
        }

        private static string Short(string cls)
        {
            int i = cls.LastIndexOf('.');
            return i < 0 ? cls : cls.Substring(i + 1);
        }

        /// <summary>"Check(\"a\",true)" → "Check"; "Ns.Class.Method" → "Method".</summary>
        private static string Method(string testName)
        {
            int p = testName.IndexOf('(');
            string n = p < 0 ? testName : testName.Substring(0, p);
            int d = n.LastIndexOf('.');
            return d < 0 ? n : n.Substring(d + 1);
        }
    }
}
