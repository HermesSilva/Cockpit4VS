using System;
using System.Security.Cryptography;
using System.Text;

namespace Tootega.Cockpit.Secrets
{
    /// <summary>
    /// TOTP (RFC 6238) — the second factor guarding the credential vault. Port of the TOTP half
    /// of src/secrets/CredentialsStore.ts.
    ///
    /// Standard parameters on purpose: SHA-1, six digits, a thirty-second step. They are what
    /// every authenticator app assumes, and a vault the user cannot enrol in a normal app is
    /// not a usable vault.
    /// </summary>
    internal static class Totp
    {
        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        private const int StepSeconds = 30;
        private const int Digits = 6;

        /// <summary>A new secret: 20 random bytes, base32-encoded as authenticators expect.</summary>
        public static string GenerateSecret()
        {
            var bytes = new byte[20];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return Base32Encode(bytes);
        }

        internal static string Base32Encode(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;

            var result = new StringBuilder((data.Length * 8 + 4) / 5);
            var bits = 0;
            var value = 0;

            foreach (var b in data)
            {
                value = (value << 8) | b;
                bits += 8;

                while (bits >= 5)
                {
                    result.Append(Base32Alphabet[(value >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }

            if (bits > 0) result.Append(Base32Alphabet[(value << (5 - bits)) & 31]);
            return result.ToString();
        }

        /// <summary>
        /// Decodes base32, ignoring padding, whitespace and any character outside the alphabet.
        /// Tolerant because users retype these by hand, often with the spacing the app showed.
        /// </summary>
        internal static byte[] Base32Decode(string text)
        {
            if (string.IsNullOrEmpty(text)) return new byte[0];

            var bytes = new System.Collections.Generic.List<byte>(text.Length * 5 / 8 + 1);
            var bits = 0;
            var value = 0;

            foreach (var c in text.ToUpperInvariant())
            {
                var index = Base32Alphabet.IndexOf(c);
                if (index < 0) continue;

                value = (value << 5) | index;
                bits += 5;

                if (bits < 8) continue;
                bytes.Add((byte)((value >> (bits - 8)) & 0xff));
                bits -= 8;
            }

            return bytes.ToArray();
        }

        /// <summary>The HOTP code for a counter.</summary>
        internal static string Hotp(string secret, long counter)
        {
            var key = Base32Decode(secret);
            if (key.Length == 0) return null;

            var buffer = new byte[8];
            for (var i = 7; i >= 0; i--)
            {
                buffer[i] = (byte)(counter & 0xff);
                counter >>= 8;
            }

            byte[] hash;
            using (var hmac = new HMACSHA1(key))
            {
                hash = hmac.ComputeHash(buffer);
            }

            var offset = hash[hash.Length - 1] & 0x0f;
            var code = ((hash[offset] & 0x7f) << 24)
                       | ((hash[offset + 1] & 0xff) << 16)
                       | ((hash[offset + 2] & 0xff) << 8)
                       | (hash[offset + 3] & 0xff);

            return (code % 1_000_000).ToString(new string('0', Digits));
        }

        /// <summary>
        /// Verifies a code, accepting one step either side to tolerate clock drift between the
        /// machine and the phone.
        ///
        /// The comparison is constant-time. It is a small thing here, but a vault that leaks
        /// how nearly-correct a guess was is not a vault.
        /// </summary>
        public static bool Verify(string secret, string code, DateTimeOffset? at = null)
        {
            if (string.IsNullOrEmpty(secret)) return false;

            var clean = (code ?? string.Empty).Replace(" ", string.Empty).Trim();
            if (clean.Length != Digits) return false;
            foreach (var c in clean)
            {
                if (c < '0' || c > '9') return false;
            }

            var seconds = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            var step = seconds / StepSeconds;

            var valid = false;
            for (var window = -1; window <= 1; window++)
            {
                var counter = step + window;
                // A negative counter would mean a clock set before 1970; there is nothing to
                // check against.
                if (counter < 0) continue;

                var expected = Hotp(secret, counter);
                // No early exit: every window is compared so the total time does not depend on
                // which one matched.
                if (expected != null && FixedTimeEquals(expected, clean)) valid = true;
            }

            return valid;
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;

            var difference = 0;
            for (var i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }

        /// <summary>The otpauth:// URI an authenticator scans.</summary>
        public static string BuildUri(string secret, string label = "Tootega Cockpit:vault",
                                      string issuer = "Tootega Cockpit")
        {
            return "otpauth://totp/" + Uri.EscapeDataString(label) +
                   "?secret=" + secret +
                   "&issuer=" + Uri.EscapeDataString(issuer) +
                   "&algorithm=SHA1&digits=" + Digits + "&period=" + StepSeconds;
        }
    }
}
