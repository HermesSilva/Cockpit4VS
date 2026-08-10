using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Tootega.Cockpit.Cli;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Stats;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// The model picker's contents: what the account can use, what each one costs, and how big
    /// its context is.
    ///
    /// Nothing here is hardcoded. The CLI has no models subcommand, so the catalogue comes from
    /// discovery; the price exists only in Anthropic's docs. A model released to the account
    /// therefore appears without an extension update — which is the whole point, and why a
    /// curated list would be the wrong design.
    /// </summary>
    internal sealed class ModelCatalog
    {
        public const string DefaultModelId = "default";

        private static readonly Regex OneMSuffix = new Regex(@"\[1m\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly StateStore _state;
        private readonly ModelPricing _pricing;

        private IReadOnlyList<DiscoveredModel> _discovered = new List<DiscoveredModel>();
        private List<string> _pickerIds = new List<string>();
        private IReadOnlyDictionary<string, PriceInfo> _prices;

        public ModelCatalog(StateStore state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _pricing = new ModelPricing();
        }

        /// <summary>The picker entries: the CLI default plus whatever the account offers.</summary>
        public IReadOnlyList<string> PickerIds => _pickerIds;

        /// <summary>
        /// Refreshes the catalogue. Best-effort: on failure the last good list is restored from
        /// the state store, so the picker shows something real while offline instead of going
        /// empty or silently stale.
        ///
        /// Returns true when the discovered contexts changed, which is the caller's cue to
        /// recompute context limits.
        /// </summary>
        public async Task<bool> RefreshAsync(string configuredApiKey)
        {
            var discoveredSomething = false;

            try
            {
                var credentials = AnthropicHttp.ResolveCredentials(configuredApiKey);
                if (credentials != null)
                {
                    var models = await ModelDiscovery.DiscoverAsync(credentials).ConfigureAwait(false);
                    if (models.Count > 0)
                    {
                        _discovered = models;
                        _pickerIds = ModelDiscovery.PickerIds(models).ToList();

                        // The REAL window per model, so the context meter stops guessing.
                        foreach (var model in models) CostModel.RegisterModelContext(model.Id, model.ContextTokens);

                        _state.Set("modelCatalog", _pickerIds);
                        discoveredSomething = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("models: discovery failed: " + ex.Message);
            }

            if (_pickerIds.Count == 0)
                _pickerIds = _state.Get<List<string>>("modelCatalog") ?? new List<string>();

            try
            {
                if (_prices == null) _prices = await _pricing.EnsureAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("models: pricing lookup failed: " + ex.Message);
            }

            return discoveredSomething;
        }

        /// <summary>
        /// Drops a model from the picker.
        ///
        /// Removal is local and not permanent: the next discovery brings back anything the
        /// account really offers. It exists to hide a custom or stale id the user pinned by
        /// hand, not to override the account.
        /// </summary>
        public void Remove(string model)
        {
            if (string.IsNullOrEmpty(model)) return;

            var baseId = OneMSuffix.Replace(model, string.Empty);
            _pickerIds = _pickerIds
                .Where(id => !string.Equals(OneMSuffix.Replace(id, string.Empty), baseId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            _state.Set("modelCatalog", _pickerIds);
        }

        /// <summary>The full option list for the picker, including whatever the tab has pinned.</summary>
        public List<string> Options(params string[] alsoInclude)
        {
            var options = new List<string> { DefaultModelId };
            options.AddRange(_pickerIds);

            foreach (var extra in alsoInclude ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(extra) || extra == DefaultModelId) continue;
                if (options.Contains(extra, StringComparer.OrdinalIgnoreCase)) continue;
                // A pinned id the catalogue does not offer still needs a row, or the picker
                // would show the session using a model that appears not to exist.
                options.Add(extra);
            }

            return options;
        }

        /// <summary>
        /// Per-model metadata for the picker columns.
        ///
        /// The label and context window are REAL, from discovery; the price comes from the
        /// docs. Absent stays absent — the UI shows nothing rather than a guess, because a
        /// wrong price on a cost panel is worse than a blank one.
        /// </summary>
        public Dictionary<string, ModelMeta> BuildMeta(IEnumerable<string> models)
        {
            var meta = new Dictionary<string, ModelMeta>(StringComparer.Ordinal);
            var mostExpensive = _prices?.Values.Select(p => p.InMTok).DefaultIfEmpty(0).Max() ?? 0;

            foreach (var id in models.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (id == DefaultModelId) continue;

                var baseId = OneMSuffix.Replace(id, string.Empty);

                var discovered = _discovered.FirstOrDefault(m =>
                    string.Equals(m.Id, baseId, StringComparison.OrdinalIgnoreCase));

                PriceInfo price = null;
                _prices?.TryGetValue(baseId, out price);

                meta[id] = new ModelMeta
                {
                    Label = discovered?.DisplayName,
                    ContextTokens = CostModel.DeriveContextLimit(id),
                    InMTok = price?.InMTok,
                    OutMTok = price?.OutMTok,
                    // Relative to the dearest model on offer, so the column reads as a
                    // comparison instead of a number nobody can calibrate.
                    PriceMult = price != null && mostExpensive > 0 ? price.InMTok / mostExpensive : (double?)null,
                };
            }

            return meta;
        }
    }
}
