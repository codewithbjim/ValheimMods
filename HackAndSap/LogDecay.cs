using System;
using UnityEngine;

namespace HackAndSap
{
    public sealed class LogDecay : MonoBehaviour
    {
        private const string WorkedKey = "HackAndSap_LogLastWorked";

        private ZNetView _nview;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
        }

        private void Start()
        {
            EnsureTimestamp();
            InvokeRepeating(nameof(CheckDecay), UnityEngine.Random.Range(15f, 45f), 60f);
        }

        private void EnsureTimestamp()
        {
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner()) return;
            ZDO zdo = _nview.GetZDO();
            if (zdo != null && zdo.GetLong(WorkedKey, 0L) <= 0L)
                zdo.Set(WorkedKey, RenewableRegistry.NowTicks());
        }

        public void MarkWorked()
        {
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner()) return;
            _nview.GetZDO()?.Set(WorkedKey, RenewableRegistry.NowTicks());
        }

        private void CheckDecay()
        {
            if (!ModConfig.Enabled.Value || ModConfig.LogDecayDays.Value <= 0f) return;
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner()) return;

            EnsureTimestamp();
            ZDO zdo = _nview.GetZDO();
            if (zdo == null) return;

            if (RenewableRegistry.IsBaseProtected(transform.position))
            {
                zdo.Set(WorkedKey, RenewableRegistry.NowTicks());
                return;
            }

            long lastWorked = zdo.GetLong(WorkedKey, 0L);
            if (lastWorked <= 0L) return;
            double ageSeconds = Math.Max(0d,
                (RenewableRegistry.NowTicks() - lastWorked) / (double)TimeSpan.TicksPerSecond);
            if (ageSeconds < ModConfig.LogDecayDays.Value * RenewableRegistry.GetDayLengthSeconds())
                return;

            ModLog.Info($"Decayed abandoned log '{gameObject.name}' away.");
            ZNetScene.instance.Destroy(gameObject);
        }
    }
}
