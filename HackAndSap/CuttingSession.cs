using UnityEngine;

namespace HackAndSap
{
    internal static class CuttingSession
    {
        private static CuttingInteraction _target;
        private static Collider _collider;
        private static Vector3 _localSurfacePoint;
        private static Player _player;
        private static ItemDrop.ItemData _axe;
        private static bool _armedForCancel;
        private static float _nextAttackAttempt;
        private static float _firstFailedAttempt = -1f;

        public static bool Active => _target != null;
        public static bool IsTarget(CuttingInteraction target) => Active && _target == target;

        public static void Begin(CuttingInteraction target, Collider collider, Vector3 surfacePoint,
            Player player, ItemDrop.ItemData axe)
        {
            if (target == null || collider == null || player == null || axe == null) return;
            if (Active) Cancel(false);

            _target = target;
            _collider = collider;
            _localSurfacePoint = collider.transform.InverseTransformPoint(surfacePoint);
            _player = player;
            _axe = axe;
            _armedForCancel = false;
            _nextAttackAttempt = 0f;
            _firstFailedAttempt = -1f;
            ModLog.Info($"Started hacking '{target.gameObject.name}' with '{axe.m_shared.m_name}'.");
        }

        public static void Tick(float deltaTime)
        {
            if (!Active) return;
            if (!StillValid())
            {
                Cancel(false);
                return;
            }

            if (!_armedForCancel)
            {
                if (!ZInput.GetButton("Use")) _armedForCancel = true;
            }
            else if (ZInput.GetButtonDown("Use"))
            {
                Cancel(false);
                return;
            }

            if (ZInput.GetButtonDown("Attack"))
            {
                Cancel(false);
                return;
            }

            if (_player.InAttack() || Time.time < _nextAttackAttempt) return;

            FaceTarget();
            bool started = _player.StartAttack(null, false);
            _nextAttackAttempt = Time.time + 0.12f;

            if (started)
            {
                _firstFailedAttempt = -1f;
                return;
            }

            if (_firstFailedAttempt < 0f) _firstFailedAttempt = Time.time;
            if (Time.time - _firstFailedAttempt > 1f)
            {
                _player.Message(MessageHud.MessageType.Center, "Unable to continue hacking");
                Cancel(false);
            }
        }

        private static Vector3 GetSurfacePoint()
        {
            return _collider.transform.TransformPoint(_localSurfacePoint);
        }

        private static bool StillValid()
        {
            if (_target == null || _collider == null || _player == null || _axe == null) return false;
            if (_player != Player.m_localPlayer || _player.IsDead()) return false;
            if (!_target.IsAvailable()) return false;
            if (_target.NetView == null || !_target.NetView.IsValid()) return false;
            if (_player.GetCurrentWeapon() != _axe || !AxeUtil.IsUsableAxe(_axe)) return false;
            if (_player.InDodge() || _player.IsStaggering() || _player.IsKnockedBack()) return false;
            if (_player.GetMoveDir().sqrMagnitude > 0.01f) return false;
            return _target.IsColliderNearby(_player, _collider);
        }

        private static void FaceTarget()
        {
            Vector3 direction = GetSurfacePoint() - _player.transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.001f)
                _player.SetLookDir(direction.normalized, 0.08f);
        }

        public static void Cancel(bool notify)
        {
            if (!Active) return;
            if (notify && _player != null)
                _player.Message(MessageHud.MessageType.Center, "Stopped hacking");

            ModLog.Info($"Stopped hacking '{_target.gameObject.name}'.");
            _target = null;
            _collider = null;
            _localSurfacePoint = Vector3.zero;
            _player = null;
            _axe = null;
            _armedForCancel = false;
            _firstFailedAttempt = -1f;
        }
    }
}
