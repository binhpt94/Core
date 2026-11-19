using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TestGate.AgentControl
{
    /// <summary>
    /// Who may write which event. TRUSTED sources are programs (hooks run by the Claude Code CLI, the git observer,
    /// the test gate). The agent itself can only produce CLAIM events: what it SAYS, shown apart from evidence.
    /// </summary>
    public static class EventTypes
    {
        public const string SessionStarted = "SESSION_STARTED";
        public const string TaskCreated = "TASK_CREATED";
        public const string TaskScopeRequested = "TASK_SCOPE_REQUESTED";
        public const string TaskApproved = "TASK_APPROVED";
        public const string TaskClosed = "TASK_CLOSED";
        public const string AgentSpawned = "AGENT_SPAWNED";
        public const string AgentStopped = "AGENT_STOPPED";
        public const string ToolUsed = "TOOL_USED";
        public const string CommandRun = "COMMAND_RUN";
        public const string FileBaseline = "FILE_BASELINE";
        public const string FileModified = "FILE_MODIFIED";
        public const string PolicyDenied = "POLICY_DENIED";
        public const string PolicyViolation = "POLICY_VIOLATION";
        public const string UnobservedChange = "UNOBSERVED_CHANGE";
        public const string GateStarted = "GATE_STARTED";
        public const string TestResult = "TEST_RESULT";
        public const string GateFinished = "GATE_FINISHED";
        public const string StopBlocked = "STOP_BLOCKED";
        public const string StopAllowed = "STOP_ALLOWED";
        public const string Claim = "CLAIM";
        public const string PlanProposed = "PLAN_PROPOSED";
        public const string RequirementAccepted = "REQUIREMENT_ACCEPTED";
        public const string TaskStale = "TASK_STALE";
        public const string ArtAccepted = "ART_ACCEPTED";
        public const string FileCleaned = "FILE_CLEANED";
        public const string HumanFixNeeded = "HUMAN_FIX_NEEDED";
        public const string CrFiled = "CR_FILED";            // task need: a module asked another one for a change (Docs/Plan/CRs)
        public const string AgentMessage = "AGENT_MESSAGE";  // a session messaged another agent/session (SendMessage): logged, never trusted as approval
    }

    public sealed class AgentEvent
    {
        public long Seq { get; set; }
        public string Ts { get; set; }
        public string Type { get; set; }
        /// <summary>Program that observed it: hook, git-observer, testgate, task-cli, agent (= untrusted claim).</summary>
        public string Source { get; set; }
        public string Session { get; set; }
        public string Agent { get; set; }
        public string Task { get; set; }
        public SortedDictionary<string, string> Data { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public string Prev { get; set; }
        public string Hash { get; set; }

        public string Get(string key) => Data.TryGetValue(key, out var v) ? v : null;

        public AgentEvent With(string key, string value)
        {
            if (value != null)
                Data[key] = value;
            return this;
        }
    }

    public sealed class ChainStatus
    {
        public bool Ok { get; set; }
        public int Count { get; set; }
        public long BrokenAt { get; set; } = -1;
        public string Message { get; set; }
        public string Head { get; set; }
    }

    /// <summary>
    /// Append-only JSON Lines with a hash chain: hash = sha256(canonical(event without hash) ), each event stores the
    /// previous hash. Editing, deleting or reordering a line breaks the chain from that line on: TAMPER-EVIDENT,
    /// not tamper-proof (an agent with shell access could rewrite the whole file; CI re-running the gate is the
    /// final authority, and the head hash can be anchored in commits).
    /// </summary>
    public sealed class EventLog
    {
        public const string Genesis = "0000000000000000";

        public string Path { get; }

        public EventLog(string path) => Path = path;

        public static EventLog For(string repoRoot) => new EventLog(System.IO.Path.Combine(repoRoot, ".agent", "events.jsonl"));

        public AgentEvent Append(AgentEvent e)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            using (AcquireLock())
            {
                var last = LastLine();
                AgentEvent prev = last == null ? null : Parse(last);
                e.Seq = prev == null ? 1 : prev.Seq + 1;
                e.Prev = prev == null ? Genesis : prev.Hash;
                e.Ts = e.Ts ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                e.Hash = ComputeHash(e);
                File.AppendAllText(Path, Serialize(e) + "\n");
                return e;
            }
        }

        public List<AgentEvent> ReadAll()
        {
            var events = new List<AgentEvent>();
            if (File.Exists(Path))
                Split(File.ReadAllBytes(Path), new List<string>(), events);
            return events;
        }

        /// <summary>Lines (for the chain check) and parsed events, from the raw bytes, in one pass.</summary>
        private static void Split(byte[] bytes, List<string> lines, List<AgentEvent> events)
        {
            int start = 0;
            for (int i = 0; i <= bytes.Length; i++)
            {
                if (i < bytes.Length && bytes[i] != (byte)'\n')
                    continue;
                var span = new ReadOnlySpan<byte>(bytes, start, i - start);
                start = i + 1;
                if (span.Trim((byte)' ').Trim((byte)'\r').Length == 0)
                    continue;
                lines.Add(Encoding.UTF8.GetString(span));
                try { events.Add(ParseUtf8(span)); }
                catch (Exception) { }
            }
        }

        public ChainStatus Verify() => ReadVerified().chain;

        public static ChainStatus Verify(IReadOnlyList<string> lines) => Verify(lines, 0, Genesis);

        /// <summary>
        /// Read the log ONCE and verify it, incrementally. A checkpoint (.agent/events.jsonl.verified) remembers how many lines
        /// were verified, their byte length, the SHA-256 of those bytes and the head hash. Next time the prefix is only hashed
        /// as bytes (milliseconds for MBs) and only new lines are re-hashed event by event. ANY change to an old byte changes
        /// the prefix digest → full verification, which then reports exactly where. Same guarantee, cost ~ new events only.
        /// </summary>
        public (List<AgentEvent> events, ChainStatus chain) ReadVerified()
        {
            var events = new List<AgentEvent>();
            if (!File.Exists(Path))
                return (events, new ChainStatus { Ok = true, Count = 0, Head = Genesis, Message = "0 events, chain intact" });
            byte[] bytes = File.ReadAllBytes(Path);
            var lines = new List<string>();
            Split(bytes, lines, events);

            int from = 0;
            string prev = Genesis;
            var cp = LoadCheckpoint();
            if (cp != null && (bytes.Length < cp.Bytes || lines.Count < cp.Count))   // append-only: it can never get shorter
                return (events, Broken(lines.Count, lines.Count + 1, "the log is SHORTER than when it was last verified (" + cp.Count + " events, head " + cp.Head +
                                                         "): events were removed or the log was replaced"));
            if (cp != null && cp.Bytes <= bytes.Length && cp.Count <= lines.Count && Digest(bytes, cp.Bytes) == cp.Sha)
            {
                from = cp.Count;
                prev = cp.Head;
            }
            var chain = Verify(lines, from, prev);
            if (chain.Ok && (cp == null || lines.Count > cp.Count))
                SaveCheckpoint(new Checkpoint { Count = lines.Count, Bytes = bytes.Length, Sha = Digest(bytes, bytes.Length), Head = chain.Head });
            return (events, chain);
        }

        private sealed class Checkpoint
        {
            public int Count { get; set; }
            public long Bytes { get; set; }
            public string Sha { get; set; }
            public string Head { get; set; }
        }

        private string CheckpointPath => Path + ".verified";

        private Checkpoint LoadCheckpoint()
        {
            try { return File.Exists(CheckpointPath) ? JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(CheckpointPath)) : null; }
            catch (Exception) { return null; }
        }

        private void SaveCheckpoint(Checkpoint c)
        {
            try { File.WriteAllText(CheckpointPath, JsonSerializer.Serialize(c)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string Digest(byte[] bytes, long count)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(bytes, 0, (int)count));
        }

        /// <param name="from">lines before this index are already verified (prefix digest matched); prev = their head hash</param>
        private static ChainStatus Verify(IReadOnlyList<string> lines, int from, string prev)
        {
            long expectedSeq = from + 1;
            for (int i = from; i < lines.Count; i++)
            {
                AgentEvent e;
                try { e = Parse(lines[i]); }
                catch (Exception ex) { return Broken(i, expectedSeq, "line " + (i + 1) + " is not a valid event (" + ex.Message + ")"); }

                if (e.Seq != expectedSeq)
                    return Broken(i, expectedSeq, "event #" + expectedSeq + " missing or reordered (found #" + e.Seq + ")");
                if (e.Prev != prev)
                    return Broken(i, e.Seq, "event #" + e.Seq + " does not follow #" + (e.Seq - 1) + " (prev hash mismatch: a line was removed, inserted or edited)");
                if (ComputeHash(e) != e.Hash)
                    return Broken(i, e.Seq, "event #" + e.Seq + " was edited after it was written (hash mismatch)");
                prev = e.Hash;
                expectedSeq++;
            }
            return new ChainStatus { Ok = true, Count = lines.Count, Head = prev, Message = lines.Count + " events, chain intact" };
        }

        private static ChainStatus Broken(int count, long seq, string message) =>
            new ChainStatus { Ok = false, Count = count, BrokenAt = seq, Message = message };

        // ---------- canonical form ----------

        public static string ComputeHash(AgentEvent e)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical(e, includeHash: false)));
                return BitConverter.ToString(bytes, 0, 16).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string Serialize(AgentEvent e) => Canonical(e, includeHash: true);

        private static string Canonical(AgentEvent e, bool includeHash)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteNumber("seq", e.Seq);
                    w.WriteString("ts", e.Ts);
                    w.WriteString("type", e.Type);
                    w.WriteString("source", e.Source);
                    w.WriteString("session", e.Session);
                    w.WriteString("agent", e.Agent);
                    w.WriteString("task", e.Task);
                    w.WriteStartObject("data");
                    foreach (var kv in e.Data)
                        w.WriteString(kv.Key, kv.Value);
                    w.WriteEndObject();
                    w.WriteString("prev", e.Prev);
                    if (includeHash)
                        w.WriteString("hash", e.Hash);
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>Same as Parse, straight from UTF-8 bytes with a forward-only reader (several times faster on big logs).</summary>
        public static AgentEvent ParseUtf8(ReadOnlySpan<byte> line)
        {
            var e = new AgentEvent();
            var r = new Utf8JsonReader(line);
            r.Read(); // {
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                string name = r.GetString();
                r.Read();
                if (name == "data" && r.TokenType == JsonTokenType.StartObject)
                {
                    while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                    {
                        string k = r.GetString();
                        r.Read();
                        if (r.TokenType == JsonTokenType.String)
                            e.Data[k] = r.GetString();
                        else
                        {
                            var copy = r;               // non-string value: keep its raw JSON, like Parse
                            using (var doc = JsonDocument.ParseValue(ref copy))
                                e.Data[k] = doc.RootElement.GetRawText();
                            r.Skip();
                        }
                    }
                    continue;
                }
                switch (name)
                {
                    case "seq": e.Seq = r.GetInt64(); break;
                    case "ts": e.Ts = Str(ref r); break;
                    case "type": e.Type = Str(ref r); break;
                    case "source": e.Source = Str(ref r); break;
                    case "session": e.Session = Str(ref r); break;
                    case "agent": e.Agent = Str(ref r); break;
                    case "task": e.Task = Str(ref r); break;
                    case "prev": e.Prev = Str(ref r); break;
                    case "hash": e.Hash = Str(ref r); break;
                    default: r.Skip(); break;
                }
            }
            return e;
        }

        private static string Str(ref Utf8JsonReader r) => r.TokenType == JsonTokenType.String ? r.GetString() : null;

        public static AgentEvent Parse(string line)
        {
            using (var doc = JsonDocument.Parse(line))
            {
                var r = doc.RootElement;
                string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var e = new AgentEvent
                {
                    Seq = r.GetProperty("seq").GetInt64(),
                    Ts = S("ts"), Type = S("type"), Source = S("source"), Session = S("session"), Agent = S("agent"), Task = S("task"),
                    Prev = S("prev"), Hash = S("hash")
                };
                if (r.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    foreach (var p in data.EnumerateObject())
                        e.Data[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                return e;
            }
        }

        // ---------- concurrency (parallel tool calls → parallel hooks) ----------

        private IDisposable AcquireLock()
        {
            string lockPath = Path + ".lock";
            for (int i = 0; ; i++)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                }
                catch (IOException) when (i < 200)
                {
                    Thread.Sleep(10);
                }
            }
        }

        private string LastLine()
        {
            if (!File.Exists(Path))
                return null;
            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length == 0)
                    return null;
                // Scan backwards for the last complete line (cheap even for large logs).
                long pos = fs.Length - 1;
                var bytes = new List<byte>();
                bool started = false;
                while (pos >= 0)
                {
                    fs.Position = pos;
                    int b = fs.ReadByte();
                    if (b == '\n')
                    {
                        if (started)
                            break;
                    }
                    else
                    {
                        started = true;
                        bytes.Add((byte)b);
                    }
                    pos--;
                }
                bytes.Reverse();
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            }
        }
    }
}
