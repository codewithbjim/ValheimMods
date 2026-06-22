using UnityEngine;

namespace CarveAndPlunder
{
    // Ragdolls live on the "effect" layer, which Valheim's interact raycast
    // (Player.m_interactMask) does not hit — so a Hoverable/Interactable on the
    // ragdoll itself is unreachable. This proxy carries a collider on the
    // "piece_nonsolid" layer (in the interact mask, walk-through, not auto-picked)
    // so the raycast finds it, and forwards every call to the real CorpseInteraction.
    public class CorpseInteractionProxy : MonoBehaviour, Hoverable, Interactable
    {
        public CorpseInteraction Target;

        public string GetHoverName() => Target != null ? Target.GetHoverName() : "";
        public string GetHoverText() => Target != null ? Target.GetHoverText() : "";

        public bool Interact(Humanoid user, bool hold, bool alt)
            => Target != null && Target.Interact(user, hold, alt);

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }
}
