using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Forms;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;

namespace RBX_Alt_Manager
{
    public class Account : IComparable<Account>
    {
        public bool Valid;
        public string SecurityToken;
        public string Username;
        public DateTime LastUse;
        private string _Alias = "";
        private string _Description = "";
        private string _Password = "";
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Group { get; set; } = "Default";
        public long UserID;
        public Dictionary<string, string> Fields = new Dictionary<string, string>();
        public DateTime LastAttemptedRefresh;
        [JsonIgnore] public DateTime PinUnlocked;
        [JsonIgnore] public DateTime TokenSet;
        [JsonIgnore] public DateTime LastAppLaunch;
        [JsonIgnore] public string CSRFToken;
        [JsonIgnore] public UserPresence Presence;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        public int CompareTo(Account compareTo)
        {
            if (compareTo == null)
                return 1;
            else
                return Group.CompareTo(compareTo.Group);
        }

        public string BrowserTrackerID;

        public string Alias
        {
            get => _Alias;
            set
            {
                if (value == null || value.Length > 50)
                    return;

                _Alias = value;
                AccountManager.SaveAccounts();
            }
        }
        public string Description
        {
            get => _Description;
            set
            {
                if (value == null || value.Length > 5000)
                    return;

                _Description = value;
                AccountManager.SaveAccounts();
            }
        }
        public string Password
        {
            get => _Password;
            set
            {
                if (value == null || value.Length > 5000)
                    return;

                _Password = value;
                AccountManager.SaveAccounts();
            }
        }

        public Account() { }

        public Account(string Cookie, string AccountJSON = null)
        {
            SecurityToken = Cookie;

            if (AccountJSON == null)
            {
                // Use modern authenticated endpoint instead of deprecated /my/account/json
                var response = AccountManager.UsersClient.Execute(MakeRequest("v1/users/authenticated", Method.Get));
                DebugLog($"[Account] UsersAPI response: status={response.StatusCode}, content={response.Content?.Substring(0, Math.Min(response.Content?.Length ?? 0, 500))}");
                AccountJSON = response.Content;
            }
            else
            {
                DebugLog($"[Account] Using provided AccountJSON: {AccountJSON?.Substring(0, Math.Min(AccountJSON?.Length ?? 0, 500))}");
            }

            if (!string.IsNullOrEmpty(AccountJSON))
            {
                // Try new format first (users.roblox.com: {"id", "name", "displayName"})
                // then fall back to old format (/my/account/json: {"UserId", "Name"})
                bool parsed = AccountJSON.TryParseJson(out JObject json);
                DebugLog($"[Account] JSON parse result: {parsed}, keys={string.Join(",", json?.Properties()?.Select(p => p.Name) ?? Array.Empty<string>())}");

                if (parsed)
                {
                    long id = json["id"]?.Value<long>() ?? json["UserId"]?.Value<long>() ?? 0;
                    string name = json["name"]?.Value<string>() ?? json["Name"]?.Value<string>();

                    DebugLog($"[Account] Parsed: id={id}, name={name}");

                    if (id > 0 && !string.IsNullOrEmpty(name))
                    {
                        UserID = id;
                        Username = name;
                        Valid = true;
                        LastUse = DateTime.Now;
                        AccountManager.LastValidAccount = this;
                    }
                    else
                    {
                        DebugLog($"[Account] INVALID: id={id}, name is null/empty={string.IsNullOrEmpty(name)}");
                    }
                }
            }
            else
            {
                DebugLog("[Account] AccountJSON is null or empty");
            }
        }

        public RestRequest MakeRequest(string url, Method method = Method.Get) => new RestRequest(url, method).AddCookie(".ROBLOSECURITY", SecurityToken, "/", ".roblox.com");

        public bool GetAuthTicket(out string Ticket)
        {
            Ticket = string.Empty;

            if (!GetCSRFToken(out string Token))
                return false;

            RestRequest request = MakeRequest("/v1/authentication-ticket/", Method.Post).AddHeader("X-CSRF-TOKEN", Token).AddHeader("Referer", "https://www.roblox.com/games/4924922222/Brookhaven-RP").AddHeader("Content-Type", "application/json");

            RestResponse response = AccountManager.AuthClient.Execute(request);

            Parameter TicketHeader = response.Headers.FirstOrDefault(x => x.Name == "rbx-authentication-ticket");

            if (TicketHeader != null)
            {
                Ticket = (string)TicketHeader.Value;
                return true;
            }

            return false;
        }

        public bool GetCSRFToken(out string Result)
        {
            RestRequest request = MakeRequest("v1/authentication-ticket/", Method.Post).AddHeader("Referer", "https://www.roblox.com/games/4924922222/Brookhaven-RP");

            RestResponse response = AccountManager.AuthClient.Execute(request);

            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                Result = $"[{(int)response.StatusCode} {response.StatusCode}] {response.Content}";
                return false;
            }

            Parameter result = response.Headers.FirstOrDefault(x => x.Name == "x-csrf-token");

            string Token = string.Empty;

            if (result != null)
            {
                Token = (string)result.Value;
                LastUse = DateTime.Now;

                AccountManager.LastValidAccount = this;
                AccountManager.SaveAccounts();
            }

            CSRFToken = Token;
            TokenSet = DateTime.Now;
            Result = Token;

            return !string.IsNullOrEmpty(Result);
        }

        public bool CheckPin(bool Internal = false)
        {
            if (!GetCSRFToken(out _))
            {
                if (!Internal) MessageBox.Show("Invalid Account Session!", "Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);

                return false;
            }

            if (DateTime.Now < PinUnlocked)
                return true;

            RestRequest request = MakeRequest("v1/account/pin/", Method.Get).AddHeader("Referer", "https://www.roblox.com/");

            RestResponse response = AccountManager.AuthClient.Execute(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                JObject pinInfo = JObject.Parse(response.Content);

                if (!pinInfo["isEnabled"].Value<bool>() || (pinInfo["unlockedUntil"].Type != JTokenType.Null && pinInfo["unlockedUntil"].Value<int>() > 0)) return true;
            }

            if (!Internal) MessageBox.Show("Pin required!", "Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);

            return false;
        }

        public bool UnlockPin(string Pin)
        {
            if (Pin.Length != 4) return false;
            if (CheckPin(true)) return true;

            if (!GetCSRFToken(out string Token)) return false;

            RestRequest request = MakeRequest("v1/account/pin/unlock", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                .AddParameter("pin", Pin);

            RestResponse response = AccountManager.AuthClient.Execute(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                JObject pinInfo = JObject.Parse(response.Content);

                if (pinInfo["isEnabled"].Value<bool>() && pinInfo["unlockedUntil"].Value<int>() > 0)
                    PinUnlocked = DateTime.Now.AddSeconds(pinInfo["unlockedUntil"].Value<int>());

                if (PinUnlocked > DateTime.Now)
                {
                    MessageBox.Show("Pin unlocked for 5 minutes", "Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    return true;
                }
            }

            return false;
        }

        public async Task<string> GetEmailJSON()
        {
            RestRequest DataRequest = MakeRequest("v1/email", Method.Get);

            RestResponse response = await AccountManager.AccountClient.ExecuteAsync(DataRequest);

            return response.Content;
        }

        public async Task<JToken> GetMobileInfo()
        {
            RestRequest DataRequest = MakeRequest("mobileapi/userinfo", Method.Get);

            RestResponse response = await AccountManager.MainClient.ExecuteAsync(DataRequest);

            if (response.StatusCode == HttpStatusCode.OK && Utilities.TryParseJson(response.Content, out JToken Data))
                return Data;

            return null;
        }

        public async Task<JToken> GetUserInfo()
        {
            RestRequest DataRequest = MakeRequest($"v1/users/{UserID}", Method.Get);

            RestResponse response = await AccountManager.UsersClient.ExecuteAsync(DataRequest);

            if (response.StatusCode == HttpStatusCode.OK && Utilities.TryParseJson(response.Content, out JToken Data))
                return Data;

            return null;
        }

        public async Task<long> GetRobux() => (await GetMobileInfo())?["RobuxBalance"]?.Value<long>() ?? 0;

        public bool SetFollowPrivacy(int Privacy)
        {
            if (!CheckPin()) return false;
            if (!GetCSRFToken(out string Token)) return false;

            RestRequest request = MakeRequest("account/settings/follow-me-privacy", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/my/account")
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded");

            switch (Privacy)
            {
                case 0:
                    request.AddParameter("FollowMePrivacy", "All");
                    break;
                case 1:
                    request.AddParameter("FollowMePrivacy", "Followers");
                    break;
                case 2:
                    request.AddParameter("FollowMePrivacy", "Following");
                    break;
                case 3:
                    request.AddParameter("FollowMePrivacy", "Friends");
                    break;
                case 4:
                    request.AddParameter("FollowMePrivacy", "NoOne");
                    break;
            }

            RestResponse response = AccountManager.MainClient.Execute(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK) return true;

            return false;
        }

        public bool ChangePassword(string Current, string New)
        {
            if (!CheckPin()) return false;
            if (!GetCSRFToken(out string Token)) return false;

            RestRequest request = MakeRequest("v2/user/passwords/change", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                .AddParameter("currentPassword", Current)
                .AddParameter("newPassword", New);

            RestResponse response = AccountManager.AuthClient.Execute(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                Password = New;

                var SToken = response.Cookies[".ROBLOSECURITY"];

                if (SToken != null)
                {
                    SecurityToken = SToken.Value;
                    AccountManager.SaveAccounts();
                }
                else
                    MessageBox.Show("An error occured while changing passwords, you will need to re-login with your new password!", "Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);

                MessageBox.Show("Password changed!", "Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);

                return true;
            }

            MessageBox.Show("Failed to change password!", "Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);

            return false;
        }

        public bool ChangeEmail(string Password, string NewEmail)
        {
            if (!CheckPin()) return false;
            if (!GetCSRFToken(out string Token)) return false;

            RestRequest request = MakeRequest("v1/email", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                .AddParameter("password", Password)
                .AddParameter("emailAddress", NewEmail);

            RestResponse response = AccountManager.AccountClient.Execute(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                MessageBox.Show("Email changed!", "Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);

                return true;
            }

            MessageBox.Show("Failed to change email, maybe your password is incorrect!", "Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);

            return false;
        }

        public bool LogOutOfOtherSessions(bool Internal = false)
        {
            if (!CheckPin(Internal)) return false;
            if (!GetCSRFToken(out string Token)) return false;

            RestRequest request = MakeRequest("authentication/signoutfromallsessionsandreauthenticate", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded");

            RestResponse response = AccountManager.MainClient.Execute(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                var SToken = response.Cookies[".ROBLOSECURITY"];

                if (SToken != null)
                {
                    SecurityToken = SToken.Value;
                    AccountManager.SaveAccounts(true);
                }
                else if (!Internal)
                    MessageBox.Show("An error occured, you will need to re-login!", "Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);

                if (!Internal) MessageBox.Show("Signed out of all other sessions!", "Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);

                return true;
            }

            if (!Internal) MessageBox.Show("Failed to log out of other sessions!", "Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);

            return false;
        }

        public bool TogglePlayerBlocked(string Username, ref bool Unblocked)
        {
            if (!CheckPin()) throw new Exception("Pin is Locked!");
            if (!AccountManager.GetUserID(Username, out long BlockeeID, out _)) throw new Exception($"Failed to obtain UserId of {Username}!");

            RestResponse BlockedResponse = GetBlockedList();

            if (!BlockedResponse.IsSuccessful) throw new Exception("Failed to obtain blocked users list!");

            string BlockedUsers = BlockedResponse.Content;

            if (!Regex.IsMatch(BlockedUsers, $"\\b{BlockeeID}\\b"))
                return BlockUserId($"{BlockeeID}").IsSuccessful;

            Unblocked = true;

            return BlockUserId($"{BlockeeID}", Unblock: true).IsSuccessful;
        }

        public RestResponse BlockUserId(string UserID, bool SkipPinCheck = false, HttpListenerContext Context = null, bool Unblock = false)
        {
            if (Context != null) Context.Response.StatusCode = 401;
            if (!SkipPinCheck && !CheckPin(true)) throw new Exception("Pin Locked");
            if (!GetCSRFToken(out string Token)) throw new Exception("Invalid X-CSRF-Token");

            RestRequest blockReq = MakeRequest($"v1/users/{UserID}/{(Unblock ? "unblock" : "block")}", Method.Post).AddHeader("X-CSRF-TOKEN", Token);

            RestResponse blockRes = AccountManager.AccountClient.Execute(blockReq);

            Program.Logger.Info($"Block Response for {UserID} | Unblocking: {Unblock}: [{blockRes.StatusCode}] {blockRes.Content}");

            if (Context != null)
                Context.Response.StatusCode = (int)blockRes.StatusCode;

            return blockRes;
        }

        public RestResponse UnblockUserId(string UserID, bool SkipPinCheck = false, HttpListenerContext Context = null) => BlockUserId(UserID, SkipPinCheck, Context, true);

        public bool UnblockEveryone(out string Response)
        {
            if (!CheckPin(true)) { Response = "Pin is Locked"; return false; }

            RestResponse response = GetBlockedList();

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                Task.Run(async () =>
                {
                    JObject List = JObject.Parse(response.Content);

                    if (List.ContainsKey("blockedUsers"))
                    {
                        foreach (var User in List["blockedUsers"])
                        {
                            if (!UnblockUserId(User["userId"].Value<string>(), true).IsSuccessful)
                            {
                                await Task.Delay(20000);

                                UnblockUserId(User["userId"].Value<string>(), true);

                                if (!CheckPin(true))
                                    break;
                            }
                        }
                    }
                });

                Response = "Unblocking Everyone";

                return true; 
            }

            Response = "Failed to unblock everyone";

            return false;
        }

        public RestResponse GetBlockedList(HttpListenerContext Context = null)
        {
            if (Context != null) Context.Response.StatusCode = 401;

            if (!CheckPin(true)) throw new Exception("Pin is Locked");

            RestRequest request = MakeRequest($"v1/users/get-detailed-blocked-users", Method.Get);

            RestResponse response = AccountManager.AccountClient.Execute(request);

            if (Context != null) Context.Response.StatusCode = (int)response.StatusCode;

            return response;
        }

        public bool ParseAccessCode(RestResponse response, out string Code)
        {
            Code = "";

            Match match = Regex.Match(response.Content, "Roblox.GameLauncher.joinPrivateGame\\(\\d+\\,\\s*'(\\w+\\-\\w+\\-\\w+\\-\\w+\\-\\w+)'");

            if (match.Success && match.Groups.Count == 2)
            {
                Code = match.Groups[1]?.Value ?? string.Empty;

                return true;
            }

            return false;
        }

        public async Task<string> JoinServer(long PlaceID, string JobID = "", bool FollowUser = false, bool JoinVIP = false, bool Internal = false) // oh god i am not refactoring everything to be async im sorry
        {
            if (string.IsNullOrEmpty(BrowserTrackerID))
            {
                Random r = new Random(UserID.GetHashCode() ^ Environment.TickCount);

                BrowserTrackerID = r.Next(100000, 175000).ToString() + r.Next(100000, 900000).ToString();
            }

            try { ClientSettingsPatcher.PatchSettings(); } catch (Exception Ex) { Program.Logger.Error($"Failed to patch ClientAppSettings: {Ex}"); }

            // Browser Join: open game page in Edge so user can solve captcha in a real browser
            // Detects share links and private server URLs in JobID
            bool isPrivateServerUrl = !string.IsNullOrEmpty(JobID) &&
                (JobID.Contains("share?code=") || JobID.Contains("privateServerLinkCode="));

            if (isPrivateServerUrl)
            {
                string shareCode = Regex.Match(JobID, @"share\?code=([^&]+)")?.Groups[1]?.Value ?? "";
                string linkCode = Regex.Match(JobID, "privateServerLinkCode=([^&]+)")?.Groups[1]?.Value ?? "";

                string gameUrl;
                if (!string.IsNullOrEmpty(shareCode))
                    gameUrl = $"https://www.roblox.com/share?code={shareCode}&type=Server";
                else if (!string.IsNullOrEmpty(linkCode))
                    gameUrl = $"https://www.roblox.com/games/{PlaceID}?privateServerLinkCode={linkCode}";
                else
                    gameUrl = JobID.StartsWith("http") ? JobID : $"https://www.roblox.com/games/{PlaceID}?privateServerLinkCode={JobID}";

                return await EdgeBrowserJoin.Launch(this, gameUrl);
            }

            if (!GetCSRFToken(out string Token)) return $"ERROR: Account Session Expired, re-add the account or try again. (Invalid X-CSRF-Token)\n{Token}";

            if (AccountManager.ShuffleJobID && string.IsNullOrEmpty(JobID))
                JobID = await Utilities.GetRandomJobId(PlaceID);

            if (GetAuthTicket(out string Ticket))
            {
                DebugLog($"[Launch] === STARTING LAUNCH for {Username} (BrowserTrackerID={BrowserTrackerID}, PlaceID={PlaceID}) ===");

                if (AccountManager.General.Get<bool>("AutoCloseLastProcess"))
                {
                    try
                    {
                        var allProcs = Process.GetProcessesByName("RobloxPlayerBeta");
                        DebugLog($"[Launch] AutoCloseLastProcess: checking {allProcs.Length} processes for TrackerID={BrowserTrackerID}");

                        foreach (Process proc in allProcs)
                        {
                            string cmdLine = proc.GetCommandLine();
                            var TrackerMatch = Regex.Match(cmdLine ?? "", @"\-b (\d+)");
                            string TrackerID = TrackerMatch.Success ? TrackerMatch.Groups[1].Value : string.Empty;

                            DebugLog($"[Launch] AutoClose check: PID={proc.Id}, TrackerID={TrackerID}, Match={TrackerID == BrowserTrackerID}");

                            if (TrackerID == BrowserTrackerID)
                            {
                                DebugLog($"[Launch] CLOSING previous process PID={proc.Id} for {Username} (same TrackerID)");
                                try // ignore ObjectDisposedExceptions
                                {
                                    proc.CloseMainWindow();
                                    await Task.Delay(250);
                                    proc.CloseMainWindow(); // Allows Roblox to disconnect from the server so we don't get the "Same account launched" error
                                    await Task.Delay(250);
                                    proc.Kill();
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception x) { Program.Logger.Error($"An error occured attempting to close {Username}'s last process(es): {x}"); }
                }

                string LinkCode = string.IsNullOrEmpty(JobID) ? string.Empty : Regex.Match(JobID, "privateServerLinkCode=([^&]+)")?.Groups[1]?.Value;
                string ShareCode = string.IsNullOrEmpty(JobID) ? string.Empty : Regex.Match(JobID, @"share\?code=([^&]+)")?.Groups[1]?.Value;
                bool IsShareLink = !string.IsNullOrEmpty(ShareCode);
                string AccessCode = IsShareLink ? string.Empty : JobID;

                // Handle new Roblox share link format: https://www.roblox.com/share?code=XXX&type=Server
                if (string.IsNullOrEmpty(LinkCode) && !string.IsNullOrEmpty(ShareCode))
                {
                    try
                    {
                        // Try Roblox share-links API to resolve the code
                        var resolveClient = new RestSharp.RestClient(new RestSharp.RestClientOptions("https://apis.roblox.com") { });

                        // Get CSRF token specifically for apis.roblox.com (auth.roblox.com token doesn't work here)
                        string apisToken = Token;
                        var csrfProbe = new RestRequest("/sharelinks/v1/resolve-link", Method.Post);
                        csrfProbe.AddCookie(".ROBLOSECURITY", SecurityToken, "/", ".roblox.com");
                        csrfProbe.AddHeader("Content-Type", "application/json");
                        csrfProbe.AddJsonBody(new { linkId = ShareCode, linkType = "Server" });
                        RestResponse csrfResponse = await resolveClient.ExecuteAsync(csrfProbe);

                        if (csrfResponse.StatusCode == HttpStatusCode.Forbidden)
                        {
                            var csrfHeader = csrfResponse.Headers?.FirstOrDefault(h => h.Name.ToLower() == "x-csrf-token");
                            if (csrfHeader != null) apisToken = (string)csrfHeader.Value;
                        }

                        var resolveRequest = new RestRequest("/sharelinks/v1/resolve-link", Method.Post);
                        resolveRequest.AddCookie(".ROBLOSECURITY", SecurityToken, "/", ".roblox.com");
                        resolveRequest.AddHeader("Content-Type", "application/json");
                        resolveRequest.AddHeader("X-CSRF-TOKEN", apisToken);
                        resolveRequest.AddJsonBody(new { linkId = ShareCode, linkType = "Server" });

                        RestResponse resolveResponse = await resolveClient.ExecuteAsync(resolveRequest);

                        if (resolveResponse.IsSuccessful && !string.IsNullOrEmpty(resolveResponse.Content))
                        {
                            // Try to extract privateServerLinkCode or accessCode from the JSON response
                            var json = JObject.Parse(resolveResponse.Content);

                            string privateLinkCode = json.SelectToken("..privateServerLinkCode")?.ToString()
                                ?? json.SelectToken("..linkCode")?.ToString()
                                ?? json.SelectToken("..privateLinkCode")?.ToString()
                                ?? "";

                            string accessCodeFromApi = json.SelectToken("..accessCode")?.ToString() ?? "";

                            if (!string.IsNullOrEmpty(privateLinkCode))
                            {
                                LinkCode = privateLinkCode;
                            }
                            else if (!string.IsNullOrEmpty(accessCodeFromApi))
                            {
                                JoinVIP = true;
                                AccessCode = accessCodeFromApi;
                            }
                        }

                        // Fallback: try following the share URL redirect chain
                        if (string.IsNullOrEmpty(LinkCode) && !JoinVIP)
                        {
                            var shareClient = new RestSharp.RestClient(new RestSharp.RestClientOptions("https://www.roblox.com") { FollowRedirects = false });
                            var shareRequest = new RestRequest($"/share-links?code={ShareCode}&type=Server", Method.Get);
                            shareRequest.AddCookie(".ROBLOSECURITY", SecurityToken, "/", ".roblox.com");

                            RestResponse shareResponse = await shareClient.ExecuteAsync(shareRequest);

                            string location = shareResponse.Headers?.FirstOrDefault(h => h.Name.ToLower() == "location")?.Value?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(location))
                            {
                                var plcMatch = Regex.Match(location, "privateServerLinkCode=([^&]+)");
                                if (plcMatch.Success) LinkCode = plcMatch.Groups[1].Value;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"[VIPJoin] Error resolving share link: {ex.Message}");
                    }

                    // If share link resolution failed completely, return error instead of proceeding with malformed URL
                    if (string.IsNullOrEmpty(LinkCode) && string.IsNullOrEmpty(AccessCode))
                        return "ERROR: Failed to resolve private server share link. The link may be invalid or expired.";
                }

                if (!string.IsNullOrEmpty(LinkCode))
                {
                    string vipUrl = string.Format("/games/{0}?privateServerLinkCode={1}", PlaceID, LinkCode);

                    RestRequest request = MakeRequest(vipUrl, Method.Get).AddHeader("X-CSRF-TOKEN", Token).AddHeader("Referer", "https://www.roblox.com/games/4924922222/Brookhaven-RP");

                    RestResponse response = await AccountManager.MainClient.ExecuteAsync(request);

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        if (ParseAccessCode(response, out string Code))
                        {
                            JoinVIP = true;
                            AccessCode = Code;
                        }
                    }
                    else if (response.StatusCode == HttpStatusCode.Redirect)
                    {
                        request = MakeRequest(string.Format("/games/{0}?privateServerLinkCode={1}", PlaceID, LinkCode), Method.Get).AddHeader("X-CSRF-TOKEN", Token).AddHeader("Referer", "https://www.roblox.com/games/4924922222/Brookhaven-RP");

                        RestResponse result = await AccountManager.Web13Client.ExecuteAsync(request);

                        if (result.StatusCode == HttpStatusCode.OK)
                        {
                            if (ParseAccessCode(result, out string Code))
                            {
                                JoinVIP = true;
                                AccessCode = Code;
                            }
                        }
                    }
                }

                if (JoinVIP)
                {
                    var request = MakeRequest("/account/settings/private-server-invite-privacy").AddHeader("X-CSRF-TOKEN", Token).AddHeader("Referer", "https://www.roblox.com/my/account");

                    RestResponse result = await AccountManager.MainClient.ExecuteAsync(request);

                    if (result.IsSuccessful && !result.Content.Contains("\"AllUsers\""))
                    {
                        AccountManager.Instance.InvokeIfRequired(() =>
                        {
                            if (Utilities.YesNoPrompt("Roblox Account Manager", "Account Manager has detected your account's privacy settings do not allow you to join private servers.", "Would you like to change this setting to Everyone now?"))
                            {
                                if (!CheckPin(true)) return;

                                var setRequest = MakeRequest("/account/settings/private-server-invite-privacy", Method.Post);

                                setRequest.AddHeader("X-CSRF-TOKEN", Token);
                                setRequest.AddHeader("Referer", "https://www.roblox.com/my/account");
                                setRequest.AddHeader("Content-Type", "application/x-www-form-urlencoded");

                                setRequest.AddParameter("PrivateServerInvitePrivacy", "AllUsers");

                                AccountManager.MainClient.Execute(setRequest);
                            }
                        });
                    }
                }

                double LaunchTime = Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds * 1000);

                if (AccountManager.UseOldJoin)
                {
                    string RPath = @"C:\Program Files (x86)\Roblox\Versions\" + AccountManager.CurrentVersion;

                    if (!Directory.Exists(RPath))
                        RPath = Path.Combine(Environment.GetEnvironmentVariable("LocalAppData"), @"Roblox\Versions\" + AccountManager.CurrentVersion);

                    if (!Directory.Exists(RPath))
                        return "ERROR: Failed to find ROBLOX executable";

                    RPath += @"\RobloxPlayerBeta.exe";

                    AccountManager.Instance.NextAccount();

                    await Task.Run(() =>
                    {
                        ProcessStartInfo Roblox = new ProcessStartInfo(RPath);
                        
                        if (JoinVIP)
                            Roblox.Arguments = string.Format("--app -t {0} -j \"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestPrivateGame&placeId={1}&accessCode={2}&linkCode={3}\"", Ticket, PlaceID, AccessCode, LinkCode);
                        else if (FollowUser)
                            Roblox.Arguments = string.Format("--app -t {0} -j \"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestFollowUser&userId={1}\"", Ticket, PlaceID);
                        else
                            Roblox.Arguments = string.Format("--app -t {0} -j \"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame{3}&placeId={1}{2}&isPlayTogetherGame=false\"", Ticket, PlaceID, "&gameId=" + JobID, string.IsNullOrEmpty(JobID) ? "" : "Job");
                    });

                    _ = Task.Run(AdjustWindowPosition);

                    return "Success";
                }
                else
                {
                    await Task.Run(() => // prevents roblox launcher hanging our main process
                    {
                        try
                        {
                            DebugLog($"[MultiRoblox] === LAUNCH START: {Username} (PlaceID={PlaceID}) ===");
                            DebugLog(SnapshotRobloxProcesses("BeforeCleanup"));

                            // Close singleton handles from existing Roblox processes before launching new one
                            CloseRobloxSingletonHandles();

                            DebugLog(SnapshotRobloxProcesses("AfterCleanup"));

                            ProcessStartInfo LaunchInfo = new ProcessStartInfo();

                            if (JoinVIP)
                                LaunchInfo.FileName = $"roblox-player:1+launchmode:play+gameinfo:{Ticket}+launchtime:{LaunchTime}+placelauncherurl:{HttpUtility.UrlEncode($"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestPrivateGame&placeId={PlaceID}&accessCode={AccessCode}&linkCode={LinkCode}")}+browsertrackerid:{BrowserTrackerID}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
                            else if (FollowUser)
                                LaunchInfo.FileName = $"roblox-player:1+launchmode:play+gameinfo:{Ticket}+launchtime:{LaunchTime}+placelauncherurl:{HttpUtility.UrlEncode($"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestFollowUser&userId={PlaceID}")}+browsertrackerid:{BrowserTrackerID}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
                            else
                                LaunchInfo.FileName = $"roblox-player:1+launchmode:play+gameinfo:{Ticket}+launchtime:{LaunchTime}+placelauncherurl:{HttpUtility.UrlEncode($"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame{(string.IsNullOrEmpty(JobID) ? "" : "Job")}&browserTrackerId={BrowserTrackerID}&placeId={PlaceID}{(string.IsNullOrEmpty(JobID) ? "" : ("&gameId=" + JobID))}&isPlayTogetherGame=false{(AccountManager.IsTeleport ? "&isTeleport=true" : "")}")}+browsertrackerid:{BrowserTrackerID}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
                            Process Launcher = Process.Start(LaunchInfo);

                            Launcher.WaitForExit();

                            DebugLog($"[MultiRoblox] Protocol launcher exited for {Username}");
                            DebugLog(SnapshotRobloxProcesses("AfterLaunch"));

                            AccountManager.Instance.NextAccount();

                            _ = Task.Run(AdjustWindowPosition);

                            // Monitor all Roblox processes for unexpected exits
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(2000); // Wait for process to stabilize
                                var monitoredProcs = Process.GetProcessesByName("RobloxPlayerBeta");
                                DebugLog($"[ExitMonitor] Watching {monitoredProcs.Length} Roblox processes after launching {Username}");
                                foreach (var mp in monitoredProcs)
                                {
                                    var pid = mp.Id;
                                    string cmdLine = "";
                                    try { cmdLine = mp.GetCommandLine() ?? ""; } catch { }
                                    var trackerMatch = Regex.Match(cmdLine, @"\-b (\d+)");
                                    string tracker = trackerMatch.Success ? trackerMatch.Groups[1].Value : "none";
                                    try
                                    {
                                        mp.EnableRaisingEvents = true;
                                        mp.Exited += (s, ev) =>
                                        {
                                            int exitCode = -1;
                                            try { exitCode = ((Process)s).ExitCode; } catch { }
                                            DebugLog($"[ExitMonitor] PROCESS EXITED: PID={pid}, Tracker={tracker}, ExitCode={exitCode}");
                                        };
                                    }
                                    catch (Exception ex) { DebugLog($"[ExitMonitor] Failed to monitor PID={pid}: {ex.Message}"); }
                                }
                            });

                            // Close handles from newly started Roblox after it creates them
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(10000);
                                DebugLog($"[MultiRoblox] === DELAYED CLEANUP (10s) for {Username} ===");
                                DebugLog(SnapshotRobloxProcesses("Before10sCleanup"));
                                CloseRobloxSingletonHandles();
                                DebugLog(SnapshotRobloxProcesses("After10sCleanup"));
                            });
                        }
                        catch (Exception x)
                        {
                            Utilities.InvokeIfRequired(AccountManager.Instance, () => MessageBox.Show($"ERROR: Failed to launch Roblox! Try re-installing Roblox.\n\n{x.Message}{x.StackTrace}", "Roblox Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Error));
                            AccountManager.Instance.CancelLaunching();
                            AccountManager.Instance.NextAccount();
                        }
                    });

                    return "Success";
                }
            }
            else
                return "ERROR: Invalid Authentication Ticket, re-add the account or try again\n(Failed to get Authentication Ticket, Roblox has probably signed you out)";
        }

        public async void AdjustWindowPosition()
        {
            if (!RobloxWatcher.RememberWindowPositions)
                return;

            if (!(int.TryParse(GetField("Window_Position_X"), out int PosX) && int.TryParse(GetField("Window_Position_Y"), out int PosY) && int.TryParse(GetField("Window_Width"), out int Width) && int.TryParse(GetField("Window_Height"), out int Height)))
                return;

            bool Found = false;
            DateTime Ends = DateTime.Now.AddSeconds(45);

            while (true)
            {
                await Task.Delay(350);

                foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta").Reverse())
                {
                    if (process.MainWindowHandle == IntPtr.Zero) continue;

                    string CommandLine = process.GetCommandLine();

                    var TrackerMatch = Regex.Match(CommandLine, @"\-b (\d+)");
                    string TrackerID = TrackerMatch.Success ? TrackerMatch.Groups[1].Value : string.Empty;

                    if (TrackerID != BrowserTrackerID) continue;

                    Found = true;

                    MoveWindow(process.MainWindowHandle, PosX, PosY, Width, Height, true);

                    break;
                }

                if (Found) break;

                if (DateTime.Now > Ends) break;
            }
        }

        private static string SnapshotRobloxProcesses(string label)
        {
            try
            {
                var procs = Process.GetProcessesByName("RobloxPlayerBeta");
                if (procs.Length == 0) return $"[Snapshot:{label}] 0 Roblox processes";
                var details = procs.Select(p =>
                {
                    try
                    {
                        bool hasWindow = p.MainWindowHandle != IntPtr.Zero;
                        string cmdLine = "";
                        try { cmdLine = p.GetCommandLine() ?? ""; } catch { cmdLine = "N/A"; }
                        var trackerMatch = Regex.Match(cmdLine, @"\-b (\d+)");
                        string tracker = trackerMatch.Success ? trackerMatch.Groups[1].Value : "none";
                        double ageSec = 0;
                        try { ageSec = (DateTime.Now - p.StartTime).TotalSeconds; } catch { }
                        return $"PID={p.Id},Window={hasWindow},Age={ageSec:F1}s,Tracker={tracker}";
                    }
                    catch { return $"PID={p.Id},Window=?,Age=?,Tracker=?"; }
                });
                return $"[Snapshot:{label}] {procs.Length} Roblox: [{string.Join(", ", details)}]";
            }
            catch { return $"[Snapshot:{label}] Error reading processes"; }
        }

        private static readonly object _debugLogLock = new object();
        private static void DebugLog(string message)
        {
            try
            {
                lock (_debugLogLock)
                {
                    File.AppendAllText(
                        Path.Combine(Environment.CurrentDirectory, "debug_log.txt"),
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
                }
            }
            catch { }
        }

        private static void CloseRobloxSingletonHandles()
        {
            try
            {
                // Kill zombie Roblox processes (no visible window = background ghost)
                // BUT protect processes younger than 30 seconds (they might still be loading)
                var allRoblox = Process.GetProcessesByName("RobloxPlayerBeta");
                DebugLog($"[MultiRoblox] CloseRobloxSingletonHandles: found {allRoblox.Length} Roblox processes");

                foreach (var proc in allRoblox)
                {
                    try
                    {
                        double ageSec = 0;
                        try { ageSec = (DateTime.Now - proc.StartTime).TotalSeconds; } catch { }

                        if (proc.MainWindowHandle == IntPtr.Zero)
                        {
                            if (ageSec < 30)
                            {
                                DebugLog($"[MultiRoblox] SKIPPING young process PID={proc.Id} (age={ageSec:F1}s, no window yet - still loading)");
                                continue;
                            }

                            DebugLog($"[MultiRoblox] Killing zombie Roblox process PID={proc.Id} (no window, age={ageSec:F1}s)");
                            proc.Kill();
                            proc.WaitForExit(3000);
                        }
                        else
                        {
                            DebugLog($"[MultiRoblox] Keeping alive PID={proc.Id} (has window, age={ageSec:F1}s)");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"[MultiRoblox] Failed to check/kill PID={proc.Id}: {ex.Message}");
                    }
                }

                string handlePath = RBX_Alt_Manager.Classes.RobloxWatcher.HandlePath;

                if (!File.Exists(handlePath))
                    File.WriteAllBytes(handlePath, RBX_Alt_Manager.Properties.Resources.handle);

                // Re-fetch after killing zombies
                var robloxProcs = Process.GetProcessesByName("RobloxPlayerBeta");
                DebugLog($"[MultiRoblox] After zombie cleanup: {robloxProcs.Length} Roblox processes remaining");
                if (robloxProcs.Length == 0) return;

                foreach (var proc in robloxProcs)
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(handlePath)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            Arguments = $"-a -p {proc.Id} -accepteula -nobanner"
                        };

                        Process handleProc = Process.Start(psi);
                        string output = handleProc.StandardOutput.ReadToEnd();
                        handleProc.WaitForExit(10000);

                        // Log ALL mutex/event/section handles to detect if Roblox renamed them
                        string[] lines = output.Split('\n');
                        int mutantCount = 0, eventCount = 0, sectionCount = 0;
                        var interestingHandles = new System.Collections.Generic.List<string>();

                        foreach (string line in lines)
                        {
                            string trimmed = line.Trim();
                            if (string.IsNullOrEmpty(trimmed)) continue;

                            // Count handle types
                            if (trimmed.Contains("Mutant")) mutantCount++;
                            if (trimmed.Contains("Event")) eventCount++;
                            if (trimmed.Contains("Section")) sectionCount++;

                            // Log anything that looks like a singleton/mutex pattern (case-insensitive)
                            string lower = trimmed.ToLower();
                            if (lower.Contains("roblox") || lower.Contains("singleton") || lower.Contains("mutex") || lower.Contains(".mtx") || lower.Contains(".shm"))
                            {
                                interestingHandles.Add(trimmed);
                            }
                        }

                        DebugLog($"[MultiRoblox] PID={proc.Id} handle summary: {mutantCount} Mutants, {eventCount} Events, {sectionCount} Sections");
                        if (interestingHandles.Count > 0)
                            DebugLog($"[MultiRoblox] PID={proc.Id} interesting handles:\n  " + string.Join("\n  ", interestingHandles));
                        else
                            DebugLog($"[MultiRoblox] PID={proc.Id} WARNING: No Roblox-related handles found! Mutex names may have changed.");

                        // Close known singleton handles
                        int closedCount = 0;
                        foreach (string line in lines)
                        {
                            string trimmed = line.Trim();
                            if (string.IsNullOrEmpty(trimmed)) continue;

                            if (trimmed.Contains("ROBLOX_singletonMutex") ||
                                trimmed.Contains("ROBLOX_singletonEvent") ||
                                trimmed.Contains("RobloxPlayerBeta.exe.mtx") ||
                                trimmed.Contains("RobloxPlayerBeta.exe.shm"))
                            {
                                string handleHex = trimmed.Split(':')[0].Trim();
                                string handleName = trimmed.Contains("singletonMutex") ? "singletonMutex" :
                                    trimmed.Contains("singletonEvent") ? "singletonEvent" :
                                    trimmed.Contains(".mtx") ? ".mtx" : ".shm";

                                ProcessStartInfo closePsi = new ProcessStartInfo(handlePath)
                                {
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    RedirectStandardOutput = true,
                                    Arguments = $"-c {handleHex} -p {proc.Id} -y -nobanner -accepteula"
                                };

                                Process closeProc = Process.Start(closePsi);
                                closeProc.WaitForExit(5000);
                                closedCount++;

                                DebugLog($"[MultiRoblox] Closed {handleName} ({handleHex}) from PID={proc.Id} => exit={closeProc.ExitCode}");
                            }
                        }

                        if (closedCount == 0)
                            DebugLog($"[MultiRoblox] PID={proc.Id} WARNING: No singleton handles were closed! Roblox may have changed mutex names.");
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"[MultiRoblox] Error closing handles for PID={proc.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[MultiRoblox] Error in CloseRobloxSingletonHandles: {ex.Message}");
            }
        }

        public string SetServer(long PlaceID, string JobID, out bool Successful)
        {
            Successful = false;

            if (!GetCSRFToken(out string Token)) return $"ERROR: Account Session Expired, re-add the account or try again. (Invalid X-CSRF-Token)\n{Token}";

            if (string.IsNullOrEmpty(Token))
                return "ERROR: Account Session Expired, re-add the account or try again. (Invalid X-CSRF-Token)";

            RestRequest request = MakeRequest("v1/join-game-instance", Method.Post).AddHeader("Content-Type", "application/json").AddJsonBody(new { gameId = JobID, placeId = PlaceID });

            RestResponse response = AccountManager.GameJoinClient.Execute(request);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                Successful = true;
                return Regex.IsMatch(response.Content, "\"joinScriptUrl\":[%s+]?null") ? response.Content : "Success";
            }
            else
                return $"Failed {response.StatusCode}: {response.Content} {response.ErrorMessage}";
        }

        public bool SendFriendRequest(string Username)
        {
            if (!AccountManager.GetUserID(Username, out long UserId, out _)) return false;
            if (!GetCSRFToken(out string Token)) return false;

            RestRequest friendRequest = MakeRequest($"/v1/users/{UserId}/request-friendship", Method.Post).AddHeader("X-CSRF-TOKEN", Token);

            RestResponse friendResponse = AccountManager.FriendsClient.Execute(friendRequest);

            return friendResponse.IsSuccessful && friendResponse.StatusCode == HttpStatusCode.OK;
        }

        public void SetDisplayName(string DisplayName)
        {
            if (!GetCSRFToken(out string Token)) return;

            RestRequest dpRequest = MakeRequest($"/v1/users/{UserID}/display-names", Method.Patch).AddHeader("X-CSRF-TOKEN", Token).AddJsonBody(new { newDisplayName = DisplayName });

            RestResponse dpResponse = AccountManager.UsersClient.Execute(dpRequest);

            if (dpResponse.StatusCode != HttpStatusCode.OK)
                throw new Exception(JObject.Parse(dpResponse.Content)?["errors"]?[0]?["message"].Value<string>() ?? $"Something went wrong\n{dpResponse.StatusCode}: {dpResponse.Content}");
        }

        public void SetAvatar(string AvatarJSONData)
        {
            if (string.IsNullOrEmpty(AvatarJSONData)) return;
            if (!AvatarJSONData.TryParseJson(out JObject Avatar)) return;
            if (Avatar == null) return;
            if (!GetCSRFToken(out string Token)) return;

            RestRequest request;

            if (Avatar.ContainsKey("playerAvatarType"))
            {
                request = MakeRequest("v1/avatar/set-player-avatar-type", Method.Post).AddHeader("X-CSRF-TOKEN", Token).AddJsonBody(new { playerAvatarType = Avatar["playerAvatarType"].Value<string>() });

                AccountManager.AvatarClient.Execute(request);
            }

            JToken ScaleObject = Avatar.ContainsKey("scales") ? Avatar["scales"] : (Avatar.ContainsKey("scale") ? Avatar["scale"] : null);

            if (ScaleObject != null)
            {
                request = MakeRequest("v1/avatar/set-scales", Method.Post).AddHeader("X-CSRF-TOKEN", Token).AddJsonBody(ScaleObject.ToString());

                AccountManager.AvatarClient.Execute(request);
            }

            if (Avatar.ContainsKey("bodyColors"))
            {
                request = MakeRequest("v1/avatar/set-body-colors", Method.Post).AddHeader("X-CSRF-TOKEN", Token).AddJsonBody(Avatar["bodyColors"].ToString());

                AccountManager.AvatarClient.Execute(request);
            }

            if (Avatar.ContainsKey("assets"))
            {
                request = MakeRequest("v2/avatar/set-wearing-assets", Method.Post).AddHeader("X-CSRF-TOKEN", Token).AddJsonBody($"{{\"assets\":{Avatar["assets"]}}}");

                RestResponse Response = AccountManager.AvatarClient.Execute(request);

                if (Response.IsSuccessful)
                {
                    var ResponseJson = JObject.Parse(Response.Content);

                    if (ResponseJson.ContainsKey("invalidAssetIds"))
                        AccountManager.Instance.InvokeIfRequired(() => new MissingAssets(this, ResponseJson["invalidAssetIds"].Select(asset => asset.Value<long>()).ToArray()).Show());
                }
            }
        }

        public async Task<bool> QuickLogIn(string Code)
        {
            if (string.IsNullOrEmpty(Code) || Code.Length != 6) return false;
            if (!GetCSRFToken(out string Token)) return false;

            using var API = new RestClient("https://apis.roblox.com/");
            var Response = await API.PostAsync(MakeRequest("auth-token-service/v1/login/enterCode").AddHeader("X-CSRF-TOKEN", Token).AddJsonBody(new { code = Code }));

            if (Response.IsSuccessful && Response.Content.TryParseJson(out dynamic Info))
                if (Utilities.YesNoPrompt("Quick Log In", "Please confirm you are logging in with this device", $"Device: {Info?.deviceInfo ?? "Unknown"}\nLocation: {Info?.location ?? "Unknown"}"))
                    return (await API.PostAsync(MakeRequest("auth-token-service/v1/login/validateCode").AddHeader("X-CSRF-TOKEN", Token).AddJsonBody(new { code = Code }))).IsSuccessful;

            return false;
        }

        public string GetField(string Name) => Fields.ContainsKey(Name) ? Fields[Name] : string.Empty;
        public void SetField(string Name, string Value) { Fields[Name] = Value; AccountManager.SaveAccounts(); }
        public void RemoveField(string Name) { Fields.Remove(Name); AccountManager.SaveAccounts(); }
    }

    public class AccountJson
    {
        public long UserId { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string UserEmail { get; set; }
        public bool IsEmailVerified { get; set; }
        public int AgeBracket { get; set; }
        public bool UserAbove13 { get; set; }
    }
}