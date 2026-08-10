using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using QRCoder;
using Tootega.Cockpit.Protocol;

namespace Tootega.Cockpit.Secrets
{
    /// <summary>Why an operation was refused. The UI says different things for each.</summary>
    internal enum VaultFailure
    {
        None,
        /// <summary>The TOTP code was wrong, or there is no enrolment to check it against.</summary>
        Totp,
        /// <summary>The data was unusable — a missing name, an unknown id.</summary>
        Input,
    }

    internal sealed class VaultResult
    {
        public bool Ok { get; set; }
        public VaultFailure Reason { get; set; }
        public string Value { get; set; }

        public static VaultResult Success(string value = null) => new VaultResult { Ok = true, Value = value };
        public static VaultResult Failed(VaultFailure reason) => new VaultResult { Ok = false, Reason = reason };
    }

    internal sealed class EnrollmentChallenge
    {
        public string QrSvg { get; set; }
        public string Secret { get; set; }
        public string Uri { get; set; }
    }

    /// <summary>
    /// A credential vault guarded by TOTP. Port of src/secrets/CredentialsStore.ts.
    ///
    /// Values live in the OS credential store and never in our own files, never in settings and
    /// never in a log. Every sensitive operation — add, edit, read, remove — requires a fresh
    /// code from an authenticator the user enrolled by scanning a QR. Listing metadata does
    /// not: seeing that a credential exists is not seeing it.
    ///
    /// The enrolment secret is held in memory until the first valid code confirms it. A secret
    /// stored before the user proved they can generate codes would lock them out of their own
    /// vault.
    /// </summary>
    internal sealed class CredentialsStore
    {
        private const string TotpKey = "creds.totp";
        private const string IndexKey = "creds.index";

        private readonly ISecretStorage _storage;
        private string _pendingTotp;

        public CredentialsStore(ISecretStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        private static string ValueKey(string id) => "creds.v." + id;

        /// <summary>Whether an authenticator is already enrolled.</summary>
        public bool IsEnrolled() => !string.IsNullOrEmpty(_storage.Get(TotpKey));

        /// <summary>Metadata for every credential. Never includes a value.</summary>
        public IReadOnlyList<CredentialMeta> List()
        {
            var raw = _storage.Get(IndexKey);
            if (string.IsNullOrEmpty(raw)) return new List<CredentialMeta>();

            return Json.TryDeserialize<List<CredentialMeta>>(raw) ?? new List<CredentialMeta>();
        }

        private void SaveIndex(IEnumerable<CredentialMeta> items)
        {
            _storage.Store(IndexKey, Json.Serialize(items.ToList()));
        }

        /// <summary>Starts enrolment: a new secret plus the QR to scan.</summary>
        public EnrollmentChallenge BeginEnroll()
        {
            var secret = Totp.GenerateSecret();
            _pendingTotp = secret;

            var uri = Totp.BuildUri(secret);
            return new EnrollmentChallenge { QrSvg = BuildQrSvg(uri), Secret = secret, Uri = uri };
        }

        /// <summary>
        /// Confirms enrolment with the authenticator's first code. Only now is the secret
        /// stored — proof that the user can actually generate codes.
        /// </summary>
        public bool ConfirmEnroll(string code)
        {
            var secret = _pendingTotp;
            if (string.IsNullOrEmpty(secret) || !Totp.Verify(secret, code)) return false;

            _storage.Store(TotpKey, secret);
            _pendingTotp = null;
            return true;
        }

        private bool Verify(string code)
        {
            var secret = _storage.Get(TotpKey);
            return !string.IsNullOrEmpty(secret) && Totp.Verify(secret, code);
        }

        public VaultResult Add(string code, string name, string value, string username = null, string note = null)
        {
            if (!Verify(code)) return VaultResult.Failed(VaultFailure.Totp);

            var trimmedName = (name ?? string.Empty).Trim();
            if (trimmedName.Length == 0 || string.IsNullOrEmpty(value)) return VaultResult.Failed(VaultFailure.Input);

            var id = NewId();
            var items = List().ToList();
            items.Add(new CredentialMeta
            {
                Id = id,
                Name = trimmedName,
                Username = Nullify(username),
                Note = Nullify(note),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            // Value first: an index entry pointing at a value that failed to store would show
            // a credential that cannot be read.
            _storage.Store(ValueKey(id), value);
            SaveIndex(items);
            return VaultResult.Success();
        }

        /// <summary>
        /// Edits a credential. An absent or empty value KEEPS the current one, which is what
        /// lets the user fix a label without retyping the secret.
        /// </summary>
        public VaultResult Edit(string code, string id, string name, string username = null,
                                string value = null, string note = null)
        {
            if (!Verify(code)) return VaultResult.Failed(VaultFailure.Totp);

            var trimmedName = (name ?? string.Empty).Trim();
            if (trimmedName.Length == 0) return VaultResult.Failed(VaultFailure.Input);

            var items = List().ToList();
            var index = items.FindIndex(c => c.Id == id);
            if (index < 0) return VaultResult.Failed(VaultFailure.Input);

            items[index].Name = trimmedName;
            items[index].Username = Nullify(username);
            items[index].Note = Nullify(note);

            if (!string.IsNullOrEmpty(value)) _storage.Store(ValueKey(id), value);

            SaveIndex(items);
            return VaultResult.Success();
        }

        /// <summary>Reads a credential's value.</summary>
        public VaultResult Use(string code, string id)
        {
            if (!Verify(code)) return VaultResult.Failed(VaultFailure.Totp);
            return VaultResult.Success(_storage.Get(ValueKey(id)) ?? string.Empty);
        }

        public VaultResult Remove(string code, string id)
        {
            if (!Verify(code)) return VaultResult.Failed(VaultFailure.Totp);

            _storage.Delete(ValueKey(id));
            SaveIndex(List().Where(c => c.Id != id));
            return VaultResult.Success();
        }

        private static string NewId()
        {
            var bytes = new byte[8];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string Nullify(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length > 0 ? trimmed : null;
        }

        /// <summary>
        /// The QR as inline SVG, so the modal renders it without a temp file or a data URI —
        /// and, more importantly, without the secret ever touching disk.
        /// </summary>
        internal static string BuildQrSvg(string uri)
        {
            try
            {
                using (var generator = new QRCodeGenerator())
                using (var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q))
                {
                    return new SvgQRCode(data).GetGraphic(4);
                }
            }
            catch (Exception ex)
            {
                // Without the QR the user can still type the secret by hand, so this degrades
                // rather than blocking enrolment.
                Util.Log.Debug("could not render the enrolment QR: " + ex.Message);
                return null;
            }
        }
    }
}
