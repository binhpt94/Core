using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TestGate.AgentControl;

namespace LogicTests.AgentControl
{
    public sealed class McpPolicyTests
    {
        [TestCase("mcp__unity__get_hierarchy", McpKind.Read)]
        [TestCase("mcp__unity__read_console", McpKind.Read)]           // a read verb first wins over "console"
        [TestCase("mcp__unity__run_tests", McpKind.Read)]
        [TestCase("mcp__unity__manage_prefabs", McpKind.Write)]
        [TestCase("mcp__unity__manage_gameobject", McpKind.Write)]
        [TestCase("mcp__unity__execute_menu_item", McpKind.Exec)]
        [TestCase("mcp__unity__script_apply_edits", McpKind.Exec)]
        [TestCase("mcp__blender__execute_blender_code", McpKind.Exec)]
        [TestCase("mcp__blender__get_scene_info", McpKind.Read)]
        [TestCase("mcp__blender__download_polyhaven_asset", McpKind.Write)]
        [TestCase("mcp__some_server__do_something_new", McpKind.Write)]
        public void TheNameDecides_UnknownMeansWrite(string tool, McpKind kind) =>
            Assert.That(McpPolicy.Classify(tool), Is.EqualTo(kind));

        [Test]
        public void TheOwnersLinesOverrideTheName_LastWins()
        {
            var o = new List<(McpKind, string)> { (McpKind.Read, "mcp__unity__read_console"), (McpKind.Deny, "mcp__blender__*") };
            Assert.That(McpPolicy.Classify("mcp__unity__read_console", o), Is.EqualTo(McpKind.Read));
            Assert.That(McpPolicy.Classify("mcp__blender__get_scene_info", o), Is.EqualTo(McpKind.Deny));
        }

        [Test]
        public void AnyKnownAgentMarker_MakesItAnAgentShell()
        {
            string Detect(params string[] vars)
            {
                var env = vars.ToDictionary(v => v, v => "1");
                return AgentShell.Detect(k => env.TryGetValue(k, out var v) ? v : null, env.Keys);
            }
            Assert.That(Detect(), Is.Null);
            Assert.That(Detect("CLAUDECODE"), Is.Not.Null);
            Assert.That(Detect("CLAUDE_CODE_ENTRYPOINT"), Is.Not.Null, "unsetting CLAUDECODE alone is not enough");
            Assert.That(Detect("CODEX_SANDBOX"), Is.Not.Null);
            Assert.That(Detect("CODEX_THREAD_ID"), Is.Not.Null);
            Assert.That(Detect("GAME_AGENT_SHELL"), Is.Not.Null);
        }
    }
}
