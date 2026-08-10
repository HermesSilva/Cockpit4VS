using System;
using System.Linq;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.Secrets;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Host
{
    /// <summary>
    /// The credentials vault, as the modal talks to it.
    ///
    /// Two rules run through every method here, and they are the reason this is its own class
    /// rather than a handful of cases in the router:
    ///
    /// A value is never logged — not the secret, not a prefix of it, not its length. Only the
    /// failure reason travels, and it is a code, not a message built from the input.
    ///
    /// Every reading operation is gated by a fresh TOTP code. "Unlocked for a while" is exactly
    /// the convenience that makes a vault pointless, so it is not offered.
    /// </summary>
    internal sealed class VaultBroker
    {
        private readonly CredentialsStore _store;
        private readonly Action<HostMessage, string> _post;

        public VaultBroker(CredentialsStore store, Action<HostMessage, string> post)
        {
            _store = store;
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        public void Handle(string tabId, WebviewMessage message)
        {
            if (_store == null)
            {
                _post(HostMessages.CredsError("Secret storage is unavailable on this machine."), tabId);
                return;
            }

            try
            {
                Route(tabId, message);
            }
            catch (Exception ex)
            {
                // Only the exception's own message: anything assembled from the payload could
                // carry a secret into the log or the UI.
                Log.Debug("vault: operation failed");
                _post(HostMessages.CredsError(ex.Message), tabId);
            }
        }

        private void Route(string tabId, WebviewMessage message)
        {
            switch (message.Kind)
            {
                case WebviewMessageKinds.CredsLoad:
                    SendState(tabId);
                    return;

                case WebviewMessageKinds.CredsEnrollBegin:
                {
                    var challenge = _store.BeginEnroll();
                    _post(HostMessages.CredsSetup(challenge.QrSvg, challenge.Secret, challenge.Uri), tabId);
                    return;
                }

                case WebviewMessageKinds.CredsEnrollConfirm:
                {
                    var ok = _store.ConfirmEnroll(message.GetString("code"));
                    _post(HostMessages.CredsResult(ok, "enroll"), tabId);
                    if (ok) SendState(tabId);
                    return;
                }

                case WebviewMessageKinds.CredsAdd:
                {
                    var result = _store.Add(message.GetString("code"), message.GetString("name"),
                                            message.GetString("value"), message.GetString("username"),
                                            message.GetString("note"));
                    Report(tabId, result, "add");
                    return;
                }

                case WebviewMessageKinds.CredsEdit:
                {
                    var result = _store.Edit(message.GetString("code"), message.GetString("id"),
                                             message.GetString("name"), message.GetString("username"),
                                             message.GetString("value"), message.GetString("note"));
                    Report(tabId, result, "edit");
                    return;
                }

                case WebviewMessageKinds.CredsUse:
                {
                    var id = message.GetString("id");
                    var result = _store.Use(message.GetString("code"), id);

                    if (!result.Ok)
                    {
                        Report(tabId, result, "use");
                        return;
                    }

                    var meta = _store.List().FirstOrDefault(c => c.Id == id);
                    _post(HostMessages.CredsValue(id, meta?.Name ?? string.Empty, result.Value ?? string.Empty), tabId);
                    return;
                }

                case WebviewMessageKinds.CredsDelete:
                {
                    Report(tabId, _store.Remove(message.GetString("code"), message.GetString("id")), "delete");
                    return;
                }
            }
        }

        private void SendState(string tabId)
        {
            _post(HostMessages.CredsData(_store.IsEnrolled(), _store.List()), tabId);
        }

        /// <summary>
        /// Answers one operation, and refreshes the list when it changed something.
        /// </summary>
        private void Report(string tabId, VaultResult result, string action)
        {
            _post(HostMessages.CredsResult(result.Ok, action, result.Ok ? null : Describe(result.Reason)), tabId);
            if (result.Ok) SendState(tabId);
        }

        /// <summary>
        /// The failure, in words the user can act on.
        ///
        /// Deliberately generic: the message is built from the failure kind alone, never from
        /// the payload, so no part of a secret can be reflected back through an error.
        /// </summary>
        private static string Describe(VaultFailure reason)
        {
            switch (reason)
            {
                case VaultFailure.Totp:
                    return "That code was not accepted. Check the authenticator app and try the next code.";

                case VaultFailure.Input:
                    return "The vault could not use that — check the name, or reopen the list.";

                default:
                    return "The operation failed.";
            }
        }
    }
}
