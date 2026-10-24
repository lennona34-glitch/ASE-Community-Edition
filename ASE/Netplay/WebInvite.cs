/*
 *
 * Invitation links through the project's website (NETPLAY.md §8, phase 5c). The website is a
 * mailbox: the host leaves its invitation there, the guest's ASE reads it and leaves its answer,
 * and the host's ASE collects that and opens its way to the guest — so neither player copies an
 * answer code by hand, which is where joining over the Internet failed in practice (§2).
 *
 * A link is https://thebitculture.com/j/<id>#<secret>, the same as a code of 20 characters. The
 * website sees the id; the secret, after the #, is what a browser never sends, and it encrypts
 * everything the website keeps (AES-256-GCM, key from HKDF over the secret, the id in the salt
 * and the associated data). What travels inside is the invitation and the answer code of
 * NetCodes.cs, unchanged: the website only spares the copying.
 *
 * The website is a convenience, never a dependency: every failure here is answered by the
 * manual codes, which keep working without it (NETPLAY.md §1).
 *
 * Nothing here touches the emulator, which is what lets tools/NetTest check it.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ASE.Netplay
{
    /// <summary>An invitation link's code: the id the website files it under and the secret
    /// that encrypts it, which never reaches the website.</summary>
    public sealed class WebCode
    {
        public const int IdLength = 8;
        public const int SecretLength = 12;
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        public string Id { get; }
        public string Secret { get; }

        /// <summary>What this side proves itself with to the website, random per code and side:
        /// the host's collects the answer or withdraws the invitation, the guest's reports how
        /// joining went. The website keeps only its hash.</summary>
        public string Key { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        WebCode(string id, string secret)
        {
            Id = id;
            Secret = secret;
        }

        public static WebCode Create() => new(RandomText(IdLength), RandomText(SecretLength));

        static string RandomText(int length)
        {
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
            return sb.ToString();
        }

        /// <summary>The code to type, in groups of four.</summary>
        public string ToText() => string.Join('-', Enumerable.Range(0, (IdLength + SecretLength) / 4).Select(i => (Id + Secret).Substring(i * 4, 4)));

        /// <summary>The link on <paramref name="web"/>, the website's address.</summary>
        public string Link(Uri web) => $"{web.GetLeftPart(UriPartial.Authority)}/j/{Id}#{Secret}";

        /// <summary>
        /// Reads a link or a 20-character code. True with the code; false with a reason when it
        /// looks like a link but is not a whole one; false with no reason when it is something
        /// else (the long manual codes, say).
        /// </summary>
        public static bool TryParse(string text, out WebCode code, out string error)
        {
            code = null;
            error = null;
            text = (text ?? "").Trim();

            int path = text.IndexOf("/j/", StringComparison.OrdinalIgnoreCase);
            if (path >= 0)
            {
                string rest = text[(path + 3)..];
                int hash = rest.IndexOf('#');
                string id = Normalise(hash < 0 ? rest : rest[..hash]).TrimEnd('/');
                string secret = hash < 0 ? "" : Normalise(rest[(hash + 1)..]);

                if (id.Length != IdLength || !id.All(Alphabet.Contains))
                {
                    error = "That link is not an ASE invitation: check that it was copied whole.";
                    return false;
                }

                if (secret.Length != SecretLength || !secret.All(Alphabet.Contains))
                {
                    error = "The link is incomplete: copy it whole, including the part after the #.";
                    return false;
                }

                code = new WebCode(id, secret);
                return true;
            }

            string plain = Normalise(text);
            if (plain.Length != IdLength + SecretLength || !plain.All(Alphabet.Contains))
                return false;

            code = new WebCode(plain[..IdLength], plain[IdLength..]);
            return true;
        }

        // Upper case, the look-alikes read as the digits they look like, separators dropped
        static string Normalise(string s) => new(s.Where(c => c != '-' && !char.IsWhiteSpace(c))
            .Select(c => char.ToUpperInvariant(c) switch { 'O' => '0', 'I' or 'L' => '1', var u => u }).ToArray());

        // ==================== Encryption ====================

        const int NonceSize = 12, TagSize = 16;

        byte[] KeyFor(string purpose) => HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.ASCII.GetBytes(Secret), 32,
            Encoding.ASCII.GetBytes("ASE web invitation " + Id), Encoding.ASCII.GetBytes(purpose));

        /// <summary>Encrypts <paramref name="text"/> for the website to keep: base64 of nonce,
        /// ciphertext and tag. <paramref name="purpose"/> ("offer", "answer") keeps one from being
        /// passed off as the other.</summary>
        public string Seal(string purpose, string text)
        {
            byte[] plain = Encoding.UTF8.GetBytes(text);
            byte[] sealedBytes = new byte[NonceSize + plain.Length + TagSize];
            var nonce = sealedBytes.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);

            using var aes = new AesGcm(KeyFor(purpose), TagSize);
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceSize, plain.Length), sealedBytes.AsSpan(NonceSize + plain.Length), Associated(purpose));
            return Convert.ToBase64String(sealedBytes);
        }

        /// <summary>What <see cref="Seal"/> made, or null when it was not made with this code.</summary>
        public string Open(string purpose, string blob)
        {
            try
            {
                byte[] b = Convert.FromBase64String(blob ?? "");
                if (b.Length < NonceSize + TagSize)
                    return null;

                byte[] plain = new byte[b.Length - NonceSize - TagSize];
                using var aes = new AesGcm(KeyFor(purpose), TagSize);
                aes.Decrypt(b.AsSpan(0, NonceSize), b.AsSpan(NonceSize, plain.Length), b.AsSpan(b.Length - TagSize), plain, Associated(purpose));
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                return null;
            }
        }

        byte[] Associated(string purpose) => Encoding.ASCII.GetBytes(Id + ":" + purpose);
    }

    public enum WebStatus
    {
        Ok,
        /// <summary>Nothing yet: ask again after <see cref="WebReply.RetryAfter"/>.</summary>
        Pending,
        /// <summary>The invitation is gone (it lasts ten minutes) or never existed.</summary>
        Expired,
        /// <summary>Somebody already answered it.</summary>
        Used,
        /// <summary>The id is taken: make another code.</summary>
        Taken,
        /// <summary>This version of ASE is no longer served (410): the manual codes only.</summary>
        Refused,
        /// <summary>The website could not be reached, or is not serving links right now.</summary>
        Unavailable,
        /// <summary>The website or the link answered something that makes no sense.</summary>
        Invalid,
    }

    public sealed record WebReply(WebStatus Status, string Text = null, string Message = null, string HostVersion = null, TimeSpan RetryAfter = default)
    {
        public bool Ok => Status == WebStatus.Ok;
    }

    /// <summary>
    /// The website's online play mailbox, as ASE uses it. Every request carries ASE's version,
    /// which lets the website send a misbehaving release back to the manual codes, and honours
    /// the pace the website asks for.
    /// </summary>
    public sealed class WebMailbox : IDisposable
    {
        static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
        static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(40);

        readonly HttpClient _http;

        public Uri Web { get; }

        public WebMailbox(Uri web, string aseVersion, HttpMessageHandler handler = null)
        {
            Web = web;
            _http = new HttpClient(handler ?? new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            })
            {
                BaseAddress = new Uri(web.GetLeftPart(UriPartial.Authority)),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            // Never thrown over: a version the website does not like is refused by it, politely
            string version = string.IsNullOrWhiteSpace(aseVersion) ? "0" : aseVersion.Trim();
            _http.DefaultRequestHeaders.UserAgent.TryParseAdd($"ASE/{version}");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-ASE-Version", version);
        }

        public void Dispose() => _http.Dispose();

        /// <summary>Host: leaves the invitation, encrypted with the code. Taken means another id is needed.</summary>
        public Task<WebReply> PublishOffer(WebCode code, string invitation, CancellationToken cancel) =>
            Send(HttpMethod.Post, "/api/net/offer", new { id = code.Id, owner = code.Key, offer = code.Seal("offer", invitation) }, null, RequestTimeout, cancel, _ => new WebReply(WebStatus.Ok));

        /// <summary>Host: takes the invitation back (it ended before anybody answered).</summary>
        public Task<WebReply> WithdrawOffer(WebCode code, CancellationToken cancel) =>
            Send(HttpMethod.Delete, $"/api/net/offer/{code.Id}", null, code.Key, RequestTimeout, cancel, _ => new WebReply(WebStatus.Ok));

        /// <summary>Guest: the invitation, decrypted (<see cref="WebReply.Text"/>), and the host's ASE version.</summary>
        public Task<WebReply> ReadOffer(WebCode code, CancellationToken cancel) =>
            Send(HttpMethod.Get, $"/api/net/offer/{code.Id}", null, null, RequestTimeout, cancel, json =>
            {
                string text = code.Open("offer", Field(json, "offer"));
                return text == null
                    ? new WebReply(WebStatus.Invalid, Message: "The link does not open its invitation: check that it was copied whole.")
                    : new WebReply(WebStatus.Ok, text, HostVersion: Field(json, "version"));
            });

        /// <summary>Guest: leaves the answer for the host, encrypted with the code.</summary>
        public Task<WebReply> PostAnswer(WebCode code, string answer, CancellationToken cancel) =>
            Send(HttpMethod.Post, $"/api/net/answer/{code.Id}", new { answer = code.Seal("answer", answer), key = code.Key }, null, RequestTimeout, cancel, _ => new WebReply(WebStatus.Ok));

        /// <summary>Host: one wait for the answer, which the website holds open up to 25 s. Ok
        /// with the answer decrypted, Pending when there was none yet.</summary>
        public Task<WebReply> WaitAnswer(WebCode code, CancellationToken cancel) =>
            Send(HttpMethod.Get, $"/api/net/answer/{code.Id}", null, code.Key, WaitTimeout, cancel, json =>
            {
                string text = code.Open("answer", Field(json, "answer"));
                return text == null
                    ? new WebReply(WebStatus.Invalid, Message: "The answer that arrived does not open with this invitation's code.")
                    : new WebReply(WebStatus.Ok, text);
            });

        /// <summary>Guest: how joining went, once, for the website's statistics — no more than
        /// these words: connected or not, by which way, and the two NAT kinds.</summary>
        public Task<WebReply> PostResult(WebCode code, bool connected, string path, NatKind hostNat, NatKind guestNat, CancellationToken cancel) =>
            Send(HttpMethod.Post, $"/api/net/result/{code.Id}", new
            {
                outcome = connected ? "connected" : "failed",
                path = path ?? "",
                host_nat = hostNat.ToString().ToLowerInvariant(),
                guest_nat = guestNat.ToString().ToLowerInvariant(),
            }, code.Key, RequestTimeout, cancel, _ => new WebReply(WebStatus.Ok));

        async Task<WebReply> Send(HttpMethod method, string path, object body, string key, TimeSpan timeout, CancellationToken cancel, Func<JsonElement, WebReply> ok)
        {
            // Serialised up front, so it goes with a Content-Length rather than chunked
            using var request = new HttpRequestMessage(method, path);
            if (body != null)
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (key != null)
                request.Headers.Add("X-ASE-Key", key);

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timer.CancelAfter(timeout);

            try
            {
                using var response = await _http.SendAsync(request, timer.Token).ConfigureAwait(false);
                string text = await response.Content.ReadAsStringAsync(timer.Token).ConfigureAwait(false);
                JsonElement json = default;
                try { json = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone(); } catch (JsonException) { }

                string message = Field(json, "error");
                var retry = response.Headers.RetryAfter?.Delta ?? (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("retry_after", out var r) && r.TryGetDouble(out double s)
                    ? TimeSpan.FromSeconds(Math.Clamp(s, 0, 3600)) : TimeSpan.Zero);

                return (int)response.StatusCode switch
                {
                    200 or 201 or 204 => ok(json),
                    202 => new WebReply(WebStatus.Pending, RetryAfter: retry),
                    404 => new WebReply(WebStatus.Expired, Message: message ?? "This invitation has expired: ask the host for a new link."),
                    409 when path == "/api/net/offer" => new WebReply(WebStatus.Taken),
                    409 => new WebReply(WebStatus.Used, Message: message ?? "This invitation has already been used: ask the host for a new link."),
                    410 => new WebReply(WebStatus.Refused, Message: message ?? "This version of ASE can no longer use invitation links: use the code."),
                    400 or 403 when response.Content.Headers.ContentType?.MediaType == "application/json" =>
                        new WebReply(WebStatus.Invalid, Message: message ?? $"The website refused the request ({(int)response.StatusCode})."),
                    _ => new WebReply(WebStatus.Unavailable, Message: Unreachable($"it answered {(int)response.StatusCode}"), RetryAfter: retry),
                };
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                return new WebReply(WebStatus.Unavailable, Message: Unreachable("it did not answer in time"));
            }
            catch (HttpRequestException ex)
            {
                return new WebReply(WebStatus.Unavailable, Message: Unreachable(ex.Message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A website is a convenience: nothing it does, or fails to do, may reach the emulator
                return new WebReply(WebStatus.Unavailable, Message: Unreachable(ex.GetType().Name));
            }
        }

        string Unreachable(string why) => $"{Web.Host} could not be reached ({why}): use the code instead.";

        static string Field(JsonElement json, string name) =>
            json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
