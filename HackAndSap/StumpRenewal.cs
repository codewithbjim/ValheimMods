using System;
using UnityEngine;

namespace HackAndSap
{
    public sealed class StumpRenewal : MonoBehaviour
    {
        private const string SourceKey = "HackAndSap_SourceTree";
        private const string BornKey = "HackAndSap_StumpBorn";

        private ZNetView _nview;
        private string _spawnSource;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            // Awake runs synchronously inside TreeBase.SpawnLog's Instantiate,
            // while the source-tree context is still available.
            _spawnSource = SpawnContext.CurrentTreePrefab;
        }

        private void Start()
        {
            EnsureState();
            InvokeRepeating(nameof(CheckRenewal), UnityEngine.Random.Range(15f, 45f), 60f);
        }

        private void EnsureState()
        {
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner()) return;
            ZDO zdo = _nview.GetZDO();
            if (zdo == null) return;

            if (string.IsNullOrEmpty(zdo.GetString(SourceKey, string.Empty)))
            {
                string source = _spawnSource;
                if (string.IsNullOrEmpty(source))
                {
                    string stumpName = RenewableRegistry.GetPrefabName(_nview, gameObject);
                    source = RenewableRegistry.InferUniqueTree(stumpName);
                }
                if (!string.IsNullOrEmpty(source)) zdo.Set(SourceKey, source);
            }

            if (zdo.GetLong(BornKey, 0L) <= 0L)
                zdo.Set(BornKey, RenewableRegistry.NowTicks());
        }

        private void CheckRenewal()
        {
            if (!ModConfig.Enabled.Value || ModConfig.StumpRenewalDays.Value <= 0f) return;
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner()) return;

            EnsureState();
            ZDO zdo = _nview.GetZDO();
            if (zdo == null) return;

            if (RenewableRegistry.IsBaseProtected(transform.position))
            {
                // Protection is durable rather than merely deferring an already
                // expired transition until the player walks away.
                zdo.Set(BornKey, RenewableRegistry.NowTicks());
                return;
            }

            long born = zdo.GetLong(BornKey, 0L);
            if (born <= 0L) return;
            double ageSeconds = Math.Max(0d,
                (RenewableRegistry.NowTicks() - born) / (double)TimeSpan.TicksPerSecond);
            if (ageSeconds < ModConfig.StumpRenewalDays.Value * RenewableRegistry.GetDayLengthSeconds())
                return;

            string source = zdo.GetString(SourceKey, string.Empty);
            GameObject saplingPrefab = RenewableRegistry.GetSapling(source);
            if (saplingPrefab == null)
            {
                ModLog.Warn($"No renewal sapling registered for stump source '{source}'.");
                return;
            }

            Quaternion rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            GameObject sapling = Instantiate(saplingPrefab, transform.position, rotation);
            ZNetView saplingView = sapling != null ? sapling.GetComponent<ZNetView>() : null;
            if (saplingView != null)
                saplingView.SetLocalScale(transform.localScale);

            ModLog.Info($"Renewed stump '{gameObject.name}' into sapling for '{source}'.");
            ZNetScene.instance.Destroy(gameObject);
        }
    }
}
