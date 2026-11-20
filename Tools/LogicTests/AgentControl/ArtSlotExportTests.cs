using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;
using Game.Core;
using NUnit.Framework;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    /// <summary>
    /// Exports every REAL art slot contract (all IArtSlotCatalog in the pure build) to Dashboard/Data/art/slots.txt, the input
    /// of `art brief`. TestGate does not compile Game.Core, so this is how it sees the contracts without parsing C#.
    /// </summary>
    public sealed class ArtSlotExport
    {
        public static List<ArtSlotSpec> FromCatalogs()
        {
            var catalogs = typeof(ArtSlotExport).Assembly.GetTypes()
                .Where(t => typeof(IArtSlotCatalog).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .Select(t => (IArtSlotCatalog)Activator.CreateInstance(t, nonPublic: true));
            return catalogs.SelectMany(c => c.Slots).Select(ToSpec).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        }

        public static ArtSlotSpec ToSpec(ArtSlotContract c)
        {
            var s = new ArtSlotSpec
            {
                Id = c.Id, Kind = c.Kind.ToString(), MinWidth = c.MinWidth, MaxWidth = c.MaxWidth, MinHeight = c.MinHeight, MaxHeight = c.MaxHeight,
                PivotXPercent = c.PivotXPercent, PivotYPercent = c.PivotYPercent, PivotTolerancePercent = c.PivotTolerancePercent,
                TickRate = c.TickRate, Intent = c.Intent
            };
            foreach (var a in c.Actions)
                s.Actions.Add(new ArtActionSpec
                {
                    Clip = a.Clip, Marker = a.Marker, MinTick = a.MinTick, MaxTick = a.MaxTick,
                    MinSpeedPercent = a.MinSpeedPercent, MaxSpeedPercent = a.MaxSpeedPercent, Required = a.Required, Frames = a.Frames
                });
            return s;
        }

        [Test]
        public void ArtSlotExport_WritesEveryDeclaredSlot()
        {
            string root = Layout.FindRoot(TestContext.CurrentContext.TestDirectory);
            var specs = FromCatalogs();
            var declared = ArtRules.Declarations(root).Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.That(specs.Select(s => s.Id), Is.EqualTo(declared), "every declared slot is exported exactly once");

            string text = ArtSlotSpecs.Format(specs);
            Assert.That(ArtSlotSpecs.Format(ArtSlotSpecs.Parse(text)), Is.EqualTo(text), "round trip");
            string path = Layout.Full(root, ArtSlotSpecs.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }
    }
}
