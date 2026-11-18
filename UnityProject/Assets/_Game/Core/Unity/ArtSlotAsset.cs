using UnityEngine;

namespace Game.Core.Presentation
{
    public enum ArtLevel
    {
        Graybox,
        Candidate,
        Final
    }

    /// <summary>
    /// Binds real art to a slot (stable id from the feature's ArtSlotContract; ARCH120 checks the id exists).
    /// Empty bindings are fine: presentation falls back to the GENERATED graybox, which always meets the contract.
    /// Art is swapped here (candidate → final) without touching gameplay. Approval is a human act (`art accept`),
    /// keyed by the bound file's content hash, so editing an approved asset makes the approval STALE.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Art Slot")]
    public sealed class ArtSlotAsset : ScriptableObject
    {
        [Tooltip("Stable slot id, e.g. MathQuiz.Slime (see the feature's *ArtSlots catalog).")]
        [SerializeField] private string slotId = "";
        [OptionalReference] [SerializeField] private GameObject candidate = null;
        [OptionalReference] [SerializeField] private GameObject final = null;

        public string SlotId => slotId;

        /// <summary>Best bound art: final, else candidate, else null (= use the graybox).</summary>
        public GameObject Resolve(out ArtLevel level)
        {
            if (final != null)
            {
                level = ArtLevel.Final;
                return final;
            }
            if (candidate != null)
            {
                level = ArtLevel.Candidate;
                return candidate;
            }
            level = ArtLevel.Graybox;
            return null;
        }

        public GameObject Binding(ArtLevel level) => level == ArtLevel.Final ? final : level == ArtLevel.Candidate ? candidate : null;
    }
}
