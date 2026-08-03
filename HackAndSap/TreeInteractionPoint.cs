using UnityEngine;

namespace HackAndSap
{
    // Mounted directly on each existing collider GameObject so Valheim's hover
    // lookup resolves this interaction on the exact surface under the crosshair.
    public sealed class TreeInteractionPoint : MonoBehaviour, Hoverable, Interactable
    {
        public CuttingInteraction Target;
        private Collider _collider;

        private void Awake()
        {
            _collider = GetComponent<Collider>();
        }

        private Collider PointCollider
            => _collider != null ? _collider : (_collider = GetComponent<Collider>());

        public string GetHoverName()
            => Target != null ? Target.GetHoverName(PointCollider) : string.Empty;

        public string GetHoverText()
            => Target != null ? Target.GetHoverText(PointCollider) : string.Empty;

        public bool Interact(Humanoid user, bool hold, bool alt)
            => Target != null && Target.Interact(user, hold, alt, PointCollider);

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }
}
