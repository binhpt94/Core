using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Core.EditorTools
{
    /// <summary>
    /// Where an art slot shows in a view, declared by the view's cases (Editor code, nothing serialized): the fixture to
    /// picture it in and how to find its objects in the instantiated view (graybox or bound art alike). ArtSnapshot uses it
    /// to picture the slot IN THE GAME on every device and to mark where it sits; ArtSnapshotTests checks every placement
    /// names a real slot and fixture and finds something.
    /// </summary>
    public sealed class ArtPlacement
    {
        public string SlotId { get; }
        public string Fixture { get; }
        public Func<GameObject, IEnumerable<RectTransform>> Find { get; }

        public ArtPlacement(string slotId, string fixture, Func<GameObject, IEnumerable<RectTransform>> find)
        {
            SlotId = slotId;
            Fixture = fixture;
            Find = find ?? throw new ArgumentNullException(nameof(find));
        }
    }
}
