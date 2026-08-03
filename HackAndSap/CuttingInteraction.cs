using UnityEngine;

namespace HackAndSap
{
    // Hoverable/Interactable lives on TreeInteractionPoint so Valheim resolves
    // the interaction on the exact collider hit by the crosshair.
    public sealed class CuttingInteraction : MonoBehaviour
    {
        private static readonly Collider[] NearbyColliders = new Collider[64];

        private TreeBase _tree;
        private TreeLog _log;
        private ZNetView _nview;

        public ZNetView NetView => _nview;

        private void Awake()
        {
            _tree = GetComponent<TreeBase>();
            _log = GetComponent<TreeLog>();
            _nview = GetComponent<ZNetView>();
        }

        public bool IsAvailable()
        {
            if (!ModConfig.Enabled.Value || _nview == null || !_nview.IsValid()) return false;
            if (_tree != null)
                return ModConfig.EnableTreeInteraction.Value && _tree.m_logPrefab != null;
            return _log != null && ModConfig.EnableLogInteraction.Value;
        }

        // The point component already tells us which exact collider Valheim's
        // crosshair ray selected. Proximity is intentionally body-based so it is
        // identical from either side and independent of third-person parallax.
        public bool IsColliderNearby(Player player, Collider collider)
        {
            if (player == null || collider == null || !collider.enabled) return false;

            int count = Physics.OverlapSphereNonAlloc(
                player.GetCenterPoint(),
                AxeUtil.GetInteractionReach(),
                NearbyColliders,
                Physics.AllLayers,
                QueryTriggerInteraction.Collide);

            for (int i = 0; i < count; ++i)
            {
                if (NearbyColliders[i] == collider) return true;
            }
            return false;
        }

        public bool TryGetCrosshairPoint(Player player, Collider collider, out Vector3 point)
        {
            point = collider != null ? collider.bounds.center : transform.position;
            if (player == null || collider == null || !collider.enabled || GameCamera.instance == null)
                return false;

            Transform cameraTransform = GameCamera.instance.transform;
            RaycastHit hit;
            if (!collider.Raycast(new Ray(cameraTransform.position, cameraTransform.forward),
                    out hit, 50f))
                return false;

            point = hit.point;
            return true;
        }

        public string GetHoverName(Collider collider)
        {
            if (!IsAvailable() || !IsColliderNearby(Player.m_localPlayer, collider))
                return string.Empty;
            return _tree != null ? "Tree" : "Felled log";
        }

        public string GetHoverText(Collider collider)
        {
            if (!IsAvailable() || !IsColliderNearby(Player.m_localPlayer, collider))
                return string.Empty;

            Player player = Player.m_localPlayer;
            string action;
            if (CuttingSession.IsTarget(this)) action = "Stop hacking";
            else if (AxeUtil.GetEquippedAxe(player) == null) action = "Equip an axe";
            else action = "Start hacking";

            return Localization.instance.Localize(
                $"[<color=yellow><b>$KEY_Use</b></color>] {action}");
        }

        public bool Interact(Humanoid user, bool hold, bool alt, Collider collider)
        {
            if (hold || alt || !IsAvailable()) return false;
            if (!(user is Player player) || player != Player.m_localPlayer) return false;

            if (CuttingSession.IsTarget(this))
            {
                CuttingSession.Cancel(false);
                return false;
            }

            ItemDrop.ItemData axe = AxeUtil.GetEquippedAxe(player);
            if (axe == null)
            {
                player.Message(MessageHud.MessageType.Center, "Equip an axe to start hacking");
                return false;
            }
            if (!IsColliderNearby(player, collider))
            {
                player.Message(MessageHud.MessageType.Center, "Move closer to start hacking");
                return false;
            }

            Vector3 point;
            TryGetCrosshairPoint(player, collider, out point);
            CuttingSession.Begin(this, collider, point, player, axe);
            return false;
        }
    }
}
