// Protocol smoke test for the private server. Plays the client side of the documented flow:
//   HTTP /email/login -> TCP CS_Login -> startup requests -> inventory/gacha/dorm requests -> reconnect.
// It never touches the user's game client. Test account credentials come from args/env, never hardcoded secrets.
//
// Usage: dotnet run --project Griffin.ProtocolSmoke -- [--http https://127.0.0.1] [--tcp 127.0.0.1:7001]
//            [--email smoke@local.test] [--password <pw>] [--scenario all|login|calendar|inventory|store|mail|profile|prefs|formation|gun|weapon|love|items|cumulative|controlroom|restored|provision|provisionlogin|mods|talents|garden|cooking|equip|lobby|display|locked|battlepass|gacha|dorm]
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Protobuf;
using Griffin.Common.Protocol;
using Griffin.Common.Proto;

var opts = ParseArgs(args);
string http = opts.GetValueOrDefault("http", "https://127.0.0.1");
string tcp = opts.GetValueOrDefault("tcp", "127.0.0.1:7001");
string email = opts.GetValueOrDefault("email", Environment.GetEnvironmentVariable("GRIFFIN_SMOKE_EMAIL") ?? "smoke@local.test");
string password = opts.GetValueOrDefault("password", Environment.GetEnvironmentVariable("GRIFFIN_SMOKE_PASSWORD") ?? "smoke-" + Environment.MachineName);
string scenario = opts.GetValueOrDefault("scenario", "all");
string? tokenFile = opts.GetValueOrDefault("token-file", null!);   // read the token from here if it exists, always write it after login
var (host, port) = (tcp.Split(':')[0], int.Parse(tcp.Split(':')[1]));

int failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " - " + detail : "")}");
    if (!ok) failures++;
}

// ---------- SDK ----------
using var httpClient = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
}) { Timeout = TimeSpan.FromSeconds(20) };

string token;
if (tokenFile is not null && File.Exists(tokenFile))
{
    token = File.ReadAllText(tokenFile).Trim();
    Console.WriteLine($"       reusing token from {tokenFile} (restart test)");
}
else
{
    using var resp = await httpClient.PostAsJsonAsync($"{http}/email/login", new { email, passwd = password });
    string body = await resp.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(body);
    int code = doc.RootElement.GetProperty("code").GetInt32();
    Check("SDK email/login", code == 0, $"code={code}");
    if (code != 0) return 1;
    token = doc.RootElement.GetProperty("data").GetProperty("access_token").GetString()!;
    if (tokenFile is not null) File.WriteAllText(tokenFile, token);
}
{
    using var req = new HttpRequestMessage(HttpMethod.Post, $"{http}/account/auto_login");
    req.Headers.TryAddWithoutValidation("Authorization", token);
    using var resp = await httpClient.SendAsync(req);
    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    Check("SDK account/auto_login with token", doc.RootElement.GetProperty("code").GetInt32() == 0);
}
{
    string json = await httpClient.GetStringAsync($"{http}/zone/gatelist");
    Check("SDK zone/gatelist", json.Contains("\"code\":0"), json);
    string advertisedEndpoint;
    using (var gateDoc = JsonDocument.Parse(json))
        advertisedEndpoint = gateDoc.RootElement.GetProperty("data")[0].GetString() ?? string.Empty;
    Check("SDK gatelist advertises the TCP endpoint under test",
        advertisedEndpoint == $"{host}:{port}", $"advertised={advertisedEndpoint}; tested={host}:{port}");
    string res = await httpClient.GetStringAsync($"{http}/zone/client_res_v1");
    Console.WriteLine("       client_res_v1: " + res[..Math.Min(res.Length, 260)]);
}

// ---------- TCP ----------

async Task<GameClient> Connect(bool reconnect)
{
    var c = new GameClient(host, port);
    c.Send(new CS_Login { Token = token, OpenId = "smoke", Reconnect = reconnect, StcVersion = "0", Platform = 1 });
    return c;
}

var main = await Connect(false);
var login = main.ReadFor(TimeSpan.FromSeconds(20));
main.Dump("login", login);
Check("login: SC_Login received", login.Any(p => p.Message is SC_Login));
Check("login: SC_Resource received", login.Any(p => p.Message is SC_Resource));
Check("login: SC_Sync received", login.Any(p => p.Message is SC_Sync));
var loginResource = login.Select(p => p.Message).OfType<SC_Resource>().FirstOrDefault();
Check("login: resource balances come from the database (item 1 = 123456789 for a new account)",
    loginResource is not null && loginResource.Res.ResourceEx.Count > 1 && loginResource.Res.ResourceEx[1] == 123456789);
Check("login: commander stamina is present as type-resource 101",
    loginResource is not null
    && loginResource.Res.StaminaTypeResource.TryGetValue(101, out var loginStamina)
    && loginStamina.Value >= 0
    && loginStamina.RefreshTime >= 0,
    loginResource?.Res.StaminaTypeResource.TryGetValue(101, out var staminaForLog) == true
        ? $"value={staminaForLog.Value}, refresh={staminaForLog.RefreshTime}"
        : "missing type-resource 101");

if (scenario is "all" or "calendar")
{
    main.Send(new CS_PlanActivity { Type = 6 });
    var activityPackets = main.ReadFor(TimeSpan.FromSeconds(3));
    var activityPlan = activityPackets.Select(p => p.Message).OfType<SC_PlanActivity>().FirstOrDefault();
    Check("calendar: type 6 responds from ActivityListData", activityPlan is not null && activityPlan.Type == 6,
        activityPlan is null ? "no SC_PlanActivity" : $"active={activityPlan.ActiveIds.Count}, next={activityPlan.NextIds.Count}");
    if (activityPlan is not null)
    {
        Check("calendar: active event details correspond to active ids",
            activityPlan.ActivityPlan.Select(p => p.Id).ToHashSet().SetEquals(activityPlan.ActiveIds));
        Check("calendar: upcoming event details correspond to next ids",
            activityPlan.WillOpenPlans.Select(p => p.Id).ToHashSet().SetEquals(activityPlan.NextIds));
        Check("calendar: event windows are not inverted",
            activityPlan.ActivityPlan.Concat(activityPlan.WillOpenPlans).All(p => p.CloseTime == 0 || p.OpenTime <= p.CloseTime));
    }

    foreach (uint type in new uint[] { 4, 5, 13 })
    {
        main.Send(new CS_PlanActivity { Type = type });
        var planPackets = main.ReadFor(TimeSpan.FromSeconds(3));
        var planActivity = planPackets.Select(p => p.Message).OfType<SC_PlanActivity>().FirstOrDefault();
        Check($"calendar: PlanData.system {type} responds with the requested type",
            planActivity is not null && planActivity.Type == type,
            planActivity is null ? "no SC_PlanActivity" : $"active={planActivity.ActiveIds.Count}, next={planActivity.NextIds.Count}");
    }

    main.Send(new CS_Plan { Type = 13 });
    var genericPlanPackets = main.ReadFor(TimeSpan.FromSeconds(3));
    var genericPlan = genericPlanPackets.Select(p => p.Message).OfType<SC_Plan>().FirstOrDefault();
    Check("calendar: generic Plan uses the same PlanData system and replies", genericPlan is not null && genericPlan.Type == 13,
        genericPlan is null ? "no SC_Plan" : $"plans={genericPlan.Plans.Count}, id={genericPlan.Id}");
    if (genericPlan is not null)
        Check("calendar: Plan exposes only valid time ranges", genericPlan.Plans.All(p => p.CloseTime == 0 || p.OpenTime <= p.CloseTime));

    main.Send(new CS_CyclePlan { Type = 1 });
    var cyclePackets = main.ReadFor(TimeSpan.FromSeconds(3));
    var cycle = cyclePackets.Select(p => p.Message).OfType<SC_CyclePlan>().FirstOrDefault();
    Check("calendar: empty CyclePlanData returns a typed empty response", cycle is not null && cycle.Type == 1 && cycle.FKFEIHHMEBF.Count == 0);

    main.Send(new CS_ActivityGetMedium());
    var mediumPackets = main.ReadFor(TimeSpan.FromSeconds(3));
    var medium = mediumPackets.Select(p => p.Message).OfType<SC_ActivityGetMedium>().FirstOrDefault();
    Check("calendar: medium response does not reuse a captured session token", medium is not null && medium.FGMEGJPEJLM.Count == 0);

    main.Send(new CS_GetActivityDailyFood());
    var foodPackets = main.ReadFor(TimeSpan.FromSeconds(3));
    var food = foodPackets.Select(p => p.Message).OfType<SC_GetActivityDailyFood>().FirstOrDefault();
    Check("calendar: missing daily-food schedule does not advertise expired claim times", food is not null && food.PLAKNGGJGPL.Count == 0);
}

if (scenario is "all" or "login")
{
    foreach (var (name, msg) in new (string, IMessage)[]
    {
        ("preface", new CS_GetPrefaceRecord()),
        ("guns", new CS_Guns()),
        ("items", new CS_Items()),
        ("sync", new CS_Sync()),
    })
    {
        main.Send(msg);
        var r = main.ReadFor(TimeSpan.FromSeconds(3));
        main.Dump(name, r);
        Check($"startup request {name} answered", r.Count > 0);
    }
}

if (scenario is "all" or "login")
{
    // A client newer than the protos sends ids the server does not know (seen: raw id 10650). The connection must survive.
    main.SendRaw(10650, [0x08, 0x01]);
    var unknown = main.ReadFor(TimeSpan.FromSeconds(2));
    main.Send(new CS_Guns());
    var afterUnknown = main.ReadFor(TimeSpan.FromSeconds(3));
    Check("unknown msg id 10650 does not close the connection", afterUnknown.Any(p => p.Message is SC_Guns));
}

if (scenario is "all" or "login")
{
    // The "welcome back" popup at login: the client waits for an (empty) reply after pressing the claim button.
    main.Send(new CS_ActivityBackOneTimeReward { EventId = 1 });
    var claim = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("welcome-back claim answered with SC_ActivityBackOneTimeReward", claim.Any(p => p.Message is SC_ActivityBackOneTimeReward));
}

if (scenario is "all" or "login")
{
    // Fallbacks for requests the server does not implement.
    main.Send(new CS_ExGetThemesInfo());
    var themes = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("fallback: unimplemented read-only CS_ExGetThemesInfo gets an empty SC reply", themes.Any(p => p.Message is SC_ExGetThemesInfo));

    main.SendRaw(10378, []);
    var rawReply = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("fallback: unknown empty request 10378 gets raw reply 10379", rawReply.Any(p => p.RawMsgId == 10379));
    Check("fallback: the raw reply 10379 carries its empty message field (an empty packet made the client throw NullReference)",
        rawReply.FirstOrDefault(p => p.RawMsgId == 10379) is { } raw10379 && raw10379.RawIMessageData.SequenceEqual(new byte[] { 0x0A, 0x00 }));

    main.Send(new CS_MomentFetch());
    var moment = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("fallback: MomentFetch gets SC_MomentFetch", moment.Any(p => p.Message is SC_MomentFetch));

    main.Send(new CS_MailDetail());
    var mailDetail = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("fallback: MailDetail of an unknown mail is answered (data or a native refusal, never silence or -10018)", mailDetail.Any(p => p.Message is SC_MailDetail || p.Message is SC_ReportError { ErrorNo: not -10018 }));

    main.Send(new CS_DevIsLogin());
    var paid = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("fallback: state-changing request (developer login check) is not auto-answered with data (only the locked error)", paid.All(p => p.Message is SC_ReportError { ErrorNo: -10018 }));
}

if (scenario is "all" or "inventory")
{
    main.Send(new CS_Guns());
    var guns = main.ReadFor(TimeSpan.FromSeconds(3));
    var gunMsg = guns.Select(p => p.Message).OfType<SC_Guns>().FirstOrDefault();
    Check("inventory: SC_Guns returned", gunMsg is not null, gunMsg is null ? "no SC_Guns" : $"{gunMsg.Guns.Count} guns");
}

if (scenario is "all" or "inventory")
{
    // /inventory is the user's own chat command (preserved from the earlier local changes).
    var (gunsPre, _) = CountOwned(main);
    main.Send(new CS_Chat { Uid = 1, Message = "/inventory addall gun" });
    var chat = main.ReadFor(TimeSpan.FromSeconds(8));
    main.Dump("chat /inventory addall gun", chat);
    var replies = chat.Select(p => p.Message).OfType<SC_Chat>().Select(m => m.Message.Message).ToList();
    Console.WriteLine("       chat replies: " + string.Join(" | ", replies));
    var (gunsPost, _) = CountOwned(main);
    Check("inventory: /inventory addall gun adds characters", gunsPost > gunsPre, $"guns {gunsPre}->{gunsPost}");
}

int known45 = loginResource is not null && loginResource.Res.ResourceEx.Count > 45 ? loginResource.Res.ResourceEx[45] : -1;

if (scenario is "all" or "store")
{
    // Table-driven purchases (StoreGoodData): price type = currency item, price = amount, reward = "item:count;".
    int bal1 = loginResource is not null ? loginResource.Res.ResourceEx[1] : -1;
    int bal45 = known45;
    main.Send(new CS_StoreBuy { GoodsId = 3011, PriceType = 1 });   // 150 of item 1 -> 1 of item 45
    var buy = main.ReadFor(TimeSpan.FromSeconds(3));
    var buyResult = buy.Select(p => p.Message).OfType<SC_StoreBuy>().FirstOrDefault();
    var updates = buy.Select(p => p.Message).OfType<SC_ResourceUpdate>().ToList();
    int Latest(uint item) => updates.Where(u => u.ResourceEx.ContainsKey(item)).Select(u => u.ResourceEx[item]).LastOrDefault(-1);
    Check("store: purchase of goods 3011 succeeds (code Normal)", buyResult is not null && (int)buyResult.Code == 0, $"code={buyResult?.Code}");
    Check("store: 150 of item 1 charged", Latest(1) == bal1 - 150, $"item1 {bal1} -> {Latest(1)}");
    Check("store: reward item 45 delivered (+1)", Latest(45) == bal45 + 1, $"item45 {bal45} -> {Latest(45)}");
    known45 = Latest(45);

    // Stamina purchase (the "+" of the top bar): goods 3006 pays gems and gives 101:60, which lives in its own stamina balance, not in resource_ex.
    int staminaBefore = loginResource is not null && loginResource.Res.StaminaTypeResource.TryGetValue(101, out var staminaLogin) ? staminaLogin.Value : -1;
    main.Send(new CS_StoreBuy { GoodsId = 3006, PriceType = 1 });
    var staminaBuy = main.ReadFor(TimeSpan.FromSeconds(3));
    var staminaUpdate = staminaBuy.Select(p => p.Message).OfType<SC_StaminaTypeResourceUpdate>().LastOrDefault();
    Check("store: buying stamina (goods 3006) adds 60 to the stamina balance and tells the client",
        staminaBuy.Select(p => p.Message).OfType<SC_StoreBuy>().FirstOrDefault() is { Code: 0 } && staminaUpdate is { ItemId: 101 } && staminaUpdate.Resource.Value == staminaBefore + 60,
        $"stamina {staminaBefore} -> {staminaUpdate?.Resource?.Value}");

    main.Send(new CS_StoreBuy { GoodsId = 1, PriceType = 11 });     // paid with real money: refused
    var cash = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StoreBuy>().FirstOrDefault();
    Check("store: goods paid with real money is refused (CostError)", cash is not null && (int)cash.Code == 4, $"code={cash?.Code}");

    // 5002: weapon 11016 for 900 of item 47 (balance 4758 at first login): five purchases fit, the sixth must be refused
    var (_, weaponsStoreBefore) = CountOwned(main);
    int ok = 0, refused = 0;
    for (int i = 0; i < 7; i++)
    {
        main.Send(new CS_StoreBuy { GoodsId = 5002, PriceType = 47 });
        var r = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StoreBuy>().FirstOrDefault();
        if (r is not null && (int)r.Code == 0) ok++;
        else if (r is not null && (int)r.Code == 4) refused++;
    }
    var (_, weaponsStoreAfter) = CountOwned(main);
    Check("store: purchases stop when the currency runs out (CostError)", ok >= 1 && refused >= 1, $"ok={ok} refused={refused}");
    Check("store: every successful purchase delivered one weapon", weaponsStoreAfter - weaponsStoreBefore == ok, $"weapons {weaponsStoreBefore} -> {weaponsStoreAfter}, ok={ok}");
}

if (scenario is "all" or "mail")
{
    // Mailbox in AccountStates: welcome mail with an attachment (item 45 x10); claim once, delete only after claiming.
    main.Send(new CS_Mails());
    var mails = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Mails>().FirstOrDefault();
    var welcome = mails?.TotalMails.FirstOrDefault();
    Check("mail: inbox has the welcome mail with an unclaimed attachment", welcome is not null && !welcome.Attached && welcome.AttachmentItems.GetValueOrDefault(45u) == 10u);

    main.Send(new CS_MailDetail { MailId = welcome?.Id ?? 1 });
    var detail = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_MailDetail>().FirstOrDefault();
    Check("mail: detail marks it read", detail is not null && detail.Mail.Read);

    main.Send(new CS_MailDel { IdList = { welcome?.Id ?? 1 } });
    var earlyDel = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_MailDel>().FirstOrDefault();
    Check("mail: a mail with an unclaimed attachment cannot be deleted", earlyDel is not null && earlyDel.IdList.Count == 0);

    main.Send(new CS_MailGetAttachment { Id = welcome?.Id ?? 1 });
    var claim1 = main.ReadFor(TimeSpan.FromSeconds(3));
    var claimResult = claim1.Select(p => p.Message).OfType<SC_MailGetAttachment>().FirstOrDefault();
    var claimUpdate = claim1.Select(p => p.Message).OfType<SC_ResourceUpdate>().LastOrDefault();
    Check("mail: claiming delivers the attachment (item 45 +10)", claimResult is not null && claimResult.Attached && claimUpdate is not null && claimUpdate.ResourceEx.GetValueOrDefault(45u) == known45 + 10,
        $"item45 {known45} -> {claimUpdate?.ResourceEx.GetValueOrDefault(45u)}");
    known45 += 10;

    main.Send(new CS_MailGetAttachment { Id = welcome?.Id ?? 1 });
    var claim2 = main.ReadFor(TimeSpan.FromSeconds(3));
    Check("mail: claiming again grants nothing (no duplicated reward)", claim2.Any(p => p.Message is SC_MailGetAttachment { Attached: true }) && !claim2.Any(p => p.Message is SC_ResourceUpdate));

    main.Send(new CS_MailDel { IdList = { welcome?.Id ?? 1 } });
    var del = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_MailDel>().FirstOrDefault();
    main.Send(new CS_Mails());
    var after = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Mails>().FirstOrDefault();
    Check("mail: claimed mail can be deleted and stays deleted", del is not null && del.IdList.Count == 1 && after is not null && after.TotalMails.Count == 0);
}

string? changedName = null;
uint changedTitle = 0;
if (scenario is "all" or "profile")
{
    // Player-card changes are stored and must come back on the next login.
    changedName = "Smoke Commander";
    main.Send(new CS_ModName { Name = changedName });
    var nameReply = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ModName>().FirstOrDefault();
    Check("profile: name change echoed", nameReply?.Name == changedName);

    main.Send(new CS_ModPlayerMotto { PlayerMotto = "protocol smoke" });
    var mottoReply = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ModPlayerMotto>().FirstOrDefault();
    Check("profile: motto change echoed", mottoReply?.PlayerMotto == "protocol smoke");

    changedTitle = 25001;
    main.Send(new CS_ModTitle { Title = changedTitle });
    var titleReply = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ModTitle>().FirstOrDefault();
    Check("profile: title change echoed", titleReply?.Title == changedTitle);

    main.Send(new CS_ModName { Name = "" });
    var badName = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ModName>().FirstOrDefault();
    Check("profile: an empty name is refused (no reply)", badName is null);
}

if (scenario is "all" or "gacha")
{
    // Table-driven banner 234001 (character banner, SSR pity 70): real roll, cost, pity and persistence.
    var (gunsBefore, weaponsBefore) = CountOwned(main);
    main.Send(new CS_GachaTentimes { GachaId = 234001 });
    var g = main.ReadFor(TimeSpan.FromSeconds(8));
    main.Dump("gacha x10", g);
    var acq = g.Select(p => p.Message).OfType<SC_GachaAcquirement>().FirstOrDefault();
    Check("gacha: SC_GachaAcquirement with 10 drops", acq is not null && acq.Drops.Count == 10 && acq.GachaTimes == 10);
    var left10 = acq?.Guarantee.GetValueOrDefault(234001u) ?? 0u;
    // 70 - pulls since the last 5-star: 60 without a 5-star in these 10 pulls, higher when one dropped and reset the counter
    Check("gacha: pity counter is at most 70 after 10 pulls (60 on a fresh account with no 5-star)", left10 <= 70, $"guarantee={left10}");
    var spent = g.Select(p => p.Message).OfType<SC_ResourceUpdate>().FirstOrDefault();
    int balance45Before = known45;
    Check("gacha: cost charged (item 45 balance before minus 10)", spent is not null && spent.ResourceEx.GetValueOrDefault(45u) == balance45Before - 10,
        $"item45 {balance45Before} -> {spent?.ResourceEx.GetValueOrDefault(45u)}");
    var weaponDrops = acq?.Drops.Count(d => d.Relate != 0) ?? 0;
    var newGuns = g.Select(p => p.Message).OfType<SC_NewGun>().Select(m => m.Gun.Id).ToList();
    var (gunsAfter, weaponsAfter) = CountOwned(main);
    Console.WriteLine($"       owned before/after: guns {gunsBefore}->{gunsAfter}, weapons {weaponsBefore}->{weaponsAfter}; weapon drops {weaponDrops}, new guns [{string.Join(",", newGuns)}]");
    Check("gacha: every weapon drop is in the inventory", weaponsAfter - weaponsBefore >= weaponDrops);
    Check("gacha: every new character is in the inventory", gunsAfter - gunsBefore == newGuns.Count);

    main.Send(new CS_GachaList());
    var gachaList = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GachaList>().FirstOrDefault();
    Check("gacha: GachaList returns the banners open now (from the table time windows)", gachaList is not null && gachaList.GachaIds.Count > 0, $"banners={gachaList?.GachaIds.Count}");

    main.Send(new CS_GachaOnetime { GachaId = 234001 });
    var one = main.ReadFor(TimeSpan.FromSeconds(4));
    var acq1 = one.Select(p => p.Message).OfType<SC_GachaAcquirement>().FirstOrDefault();
    var left1 = acq1?.Guarantee.GetValueOrDefault(234001u) ?? 0u;
    Check("gacha: single pull gives 1 drop and the pity counter moves by one (or resets on a 5-star)",
        acq1 is not null && acq1.Drops.Count == 1 && (left1 == left10 - 1 || left1 == 70u), $"drops={acq1?.Drops.Count} guarantee {left10}->{left1}");
}

if (scenario is "gachaall")
{
    // Every table-driven banner must roll without errors (pools of every type: characters, weapons, novice, items).
    string idsFile = opts.GetValueOrDefault("ids-file", "gacha_ids.txt");
    int okBanners = 0;
    var failed = new List<uint>();
    foreach (uint id in File.ReadAllLines(idsFile).Where(l => l.Length > 0).Select(uint.Parse))
    {
        main.Send(new CS_GachaTentimes { GachaId = id });
        var r = main.ReadFor(TimeSpan.FromSeconds(1.2));
        var a = r.Select(p => p.Message).OfType<SC_GachaAcquirement>().FirstOrDefault();
        if (a is not null && a.Drops.Count == 10) okBanners++; else failed.Add(id);
    }
    Check($"gachaall: every table banner rolled 10 drops ({okBanners} ok)", failed.Count == 0, failed.Count == 0 ? "" : "failed ids: " + string.Join(",", failed.Take(20)));
}

if (scenario is "all" or "prefs")
{
    // Preferences with no dedicated handler: stored per account and echoed (PreferenceResponder).
    main.Send(new CS_SetBgm { JNHHAINDALC = 7 });
    var bgm = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SetBgm>().FirstOrDefault();
    Check("prefs: SetBgm echoes the value", bgm?.JNHHAINDALC == 7);

    var meiling = new CS_SetLobbyMeilingConfig();
    meiling.ECGAHCPCDDI.AddRange([3u, 1u, 4u]);
    main.Send(meiling);
    var meilingReply = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SetLobbyMeilingConfig>().FirstOrDefault();
    Check("prefs: repeated field is echoed", meilingReply is not null && meilingReply.ECGAHCPCDDI.SequenceEqual([3u, 1u, 4u]));

    main.Send(new CS_LobbyAsmrUnlockScene { IAAPPCGFLMD = 5 });
    var asmr = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyAsmrUnlockScene>().FirstOrDefault();
    Check("echo: a lobby unlock request with no handler is echoed (EchoResponder)", asmr?.IAAPPCGFLMD == 5);

    main.Send(new CS_LobbyPartyLeave());
    var leave = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyPartyLeave>().FirstOrDefault();
    Check("ack: a request whose response is an empty message is acknowledged (AckResponder)", leave is not null);

    main.Send(new CS_LobbyCharacterClaimReward { StoryId = 3 });
    var claimed = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyCharacterClaimReward>().FirstOrDefault();
    Check("echo/ack: a request that names a reward is NOT answered without a real handler", claimed is null);

    main.Send(new CS_SetNameCardTheme { ThemeId = 12 });
    var theme = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SetNameCardTheme>().FirstOrDefault();
    Check("prefs: SetNameCardTheme echoes the value", theme?.ThemeId == 12);
}

if (scenario is "all" or "gun")
{
    // Character progression: level (paid with resources 25 and 2), class cap, class promotion, grade.
    main.Send(new CS_Guns());
    var gunList = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().FirstOrDefault();
    // the lowest-level character: the order of the list is not fixed and a level-60 one would refuse the level-up checks below
    var gun0 = gunList?.Guns.OrderBy(g => g.Level).FirstOrDefault();
    Check("gun: the account has a character to level", gun0 is not null);

    // Remoulding core (weapon panel): owned from level 55 (A-54), absent below it; raising it is accepted and kept
    var high = gunList?.Guns.FirstOrDefault(g => g.Level >= 55);
    var low = gunList?.Guns.FirstOrDefault(g => g.Level < 55);
    Check("core: a character under level 55 has no core (the client then shows the 55-level toast)", low is null || low.GrassGroup.Count == 0);
    if (high is not null)
    {
        Check("core: a character of level 55 or more has its core", high.GrassGroup.Count == 1 && high.GrassGroup[0].CoreLevel != 0 && high.GrassGroup[0].Idx == 1, $"cores={high.GrassGroup.Count}");
        // the client reads field 1 (proto "idx") as the level and field 3 (proto "core_level") as the core id
        uint coreId = high.GrassGroup[0].CoreLevel;
        uint coreLevel = high.GrassGroup[0].Idx;
        main.Send(new CS_GunRemouldingCoreBelong { KCMKNJMDAIE = coreId, GunId = high.Id });
        var belong = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunRemouldingCoreBelong>().FirstOrDefault();
        Check("core: equipping the core of the character is confirmed", belong is not null && belong.KCMKNJMDAIE == coreId && belong.GunId == high.Id);
        main.Send(new CS_GunRemouldingCoreLvUp { CoreLevel = coreId, Idx = coreLevel + 1, GunId = high.Id });
        var coreUp = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunRemouldingCoreLvUp>().FirstOrDefault();
        Check("core: raising the core is answered with the new level", coreUp is not null && coreUp.Idx == coreLevel + 1 && coreUp.CoreLevel == coreId, $"level={coreUp?.Idx}");
        main.Send(new CS_Guns());
        var coreAfter = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().FirstOrDefault()?.Guns.FirstOrDefault(g => g.Id == high.Id);
        Check("core: the core level is kept", coreAfter is not null && coreAfter.GrassGroup.Count == 1 && coreAfter.GrassGroup[0].Idx == coreLevel + 1 && coreAfter.GrassGroup[0].CoreLevel == coreId);
        Check("core: the reserved storage key is not sent as talent consumption", coreAfter is not null && !coreAfter.GunTalentConsume.ContainsKey(0xFFFF0001u));
        // plugins placed in the six slots of the core are saved (they came off at every login before, A-58)
        main.Send(new CS_GunRemouldingPlugins());
        var pluginList = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunRemouldingPlugins>().FirstOrDefault()?.RemouldingPlugins;
        ulong pluginA = pluginList?.ElementAtOrDefault(0)?.Id ?? 0, pluginB = pluginList?.ElementAtOrDefault(1)?.Id ?? 0;
        Check("core plugins: the catalog lists plugins", pluginA != 0 && pluginB != 0 && pluginA != pluginB);
        main.Send(new CS_GunRemouldingPluginBelong { GunId = high.Id, Slot = 1, GIBKJMJPLGC = pluginA });
        var belongReply = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunRemouldingPluginBelong>().FirstOrDefault();
        Check("core plugins: equipping a plugin is confirmed", belongReply is not null && belongReply.Slot == 1 && belongReply.GIBKJMJPLGC == pluginA);
        main.Send(new CS_GunRemouldingPluginBelong { GunId = high.Id, Slot = 3, GIBKJMJPLGC = pluginB });
        main.ReadFor(TimeSpan.FromSeconds(1));
        main.Send(new CS_Guns());
        var withPlugins = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().FirstOrDefault()?.Guns.FirstOrDefault(g => g.Id == high.Id);
        Check("core plugins: the equipped plugins come back in the next gun list (they survive a restart of the game)",
            withPlugins is { GrassGroup.Count: 1 } && withPlugins.GrassGroup[0].PluginUids.Count == 6 && withPlugins.GrassGroup[0].PluginUids[0] == pluginA
            && withPlugins.GrassGroup[0].PluginUids[2] == pluginB && withPlugins.GrassGroup[0].PluginUids[1] == 0, $"slots={string.Join(",", withPlugins?.GrassGroup.FirstOrDefault()?.PluginUids ?? [])}");
        main.Send(new CS_GunRemouldingPlugins());
        var pluginsAfter = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunRemouldingPlugins>().FirstOrDefault()?.RemouldingPlugins;
        Check("core plugins: the plugin list says which character holds each one", pluginsAfter?.FirstOrDefault(p => p.Id == pluginA)?.GunId == high.Id && pluginsAfter?.FirstOrDefault(p => p.Id == pluginB)?.GunId == high.Id);
        var batch = new CS_GunRemouldingPluginBatchBelong { GunId = high.Id };
        batch.PluginUids.AddRange([pluginB, pluginA]);
        main.Send(batch);
        var batchReply = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunRemouldingPluginBatchBelong>().FirstOrDefault();
        Check("core plugins: the one-click equip fills the slots in order", batchReply is not null && batchReply.PluginUids.Count == 6 && batchReply.PluginUids[0] == pluginB && batchReply.PluginUids[1] == pluginA && batchReply.PluginUids[2] == 0);
        main.Send(new CS_GunRemouldingPluginBelong { GunId = high.Id, Slot = 0, GIBKJMJPLGC = pluginB });
        main.ReadFor(TimeSpan.FromSeconds(1));
        main.Send(new CS_GunRemouldingPluginBatchBelong { GunId = high.Id });
        var emptied = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunRemouldingPluginBatchBelong>().FirstOrDefault();
        Check("core plugins: taking everything off empties the six slots", emptied is not null && emptied.PluginUids.All(u => u == 0));
        main.Send(new CS_GunRemouldingPluginBelong { GunId = high.Id, Slot = 7, GIBKJMJPLGC = pluginA });
        Check("core plugins: a slot above 6 is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_GunRemouldingPluginBelong));

        main.Send(new CS_GunRemouldingCoreLvUp { CoreLevel = coreId + 1, Idx = 2, GunId = high.Id });
        var wrong = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunRemouldingCoreLvUp>().FirstOrDefault();
        Check("core: a core that is not the character's is refused", wrong is null);
    }
    else
        Console.WriteLine("       (no character of level 55 on this account: core checks skipped)");
    if (gun0 is not null)
    {
        uint id = gun0.Id;
        main.Send(new CS_GunLevelUp { GunId = id, Level = 20 });
        var up = main.ReadFor(TimeSpan.FromSeconds(3));
        var lv = up.Select(p => p.Message).OfType<SC_GunLevelUp>().FirstOrDefault();
        var paid = up.Select(p => p.Message).OfType<SC_ResourceUpdate>().FirstOrDefault();
        Check("gun: level-up to 20 answered with the new level", lv is not null && lv.Level == 20 && lv.GunId == id, $"level={lv?.Level}");
        Check("gun: battle reports (resource 25) were charged", paid is not null && paid.ResourceEx.ContainsKey(25u));

        main.Send(new CS_GunLevelUp { GunId = id, Level = 60 });
        var cappedMessages = main.ReadFor(TimeSpan.FromSeconds(2));
        var capped = cappedMessages.Select(p => p.Message).OfType<SC_GunLevelUp>().FirstOrDefault();
        Check("gun: the class-1 cap (20) blocks further levels", capped is null);
        var cappedError = cappedMessages.Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
        Check("gun: the refusal is reported with GunLevelMax (-10105) for CS_GunLevelUp",
            cappedError is not null && cappedError.ErrorNo == -10105 && cappedError.ProtocolId == (uint)MsgId.MsgCsGunLevelUp, $"error={cappedError?.ErrorNo} protocol={cappedError?.ProtocolId}");

        main.Send(new CS_GunClassUpdate { GunId = id });
        var clsMessages = main.ReadFor(TimeSpan.FromSeconds(2));
        var cls = clsMessages.Select(p => p.Message).OfType<SC_GunClassUpdate>().FirstOrDefault();
        Check("gun: class promotion at the cap succeeds (class 2)", cls is not null && cls.GunClass == 2, $"class={cls?.GunClass}");
        Check("gun: the promotion charged its materials (GunClassData: 4 x 8328 and 1000 gold)",
            clsMessages.Select(p => p.Message).OfType<SC_Items>().Any(i => i.Items.TryGetValue(8328u, out int left) && left == 96)
            && clsMessages.Select(p => p.Message).OfType<SC_ResourceUpdate>().Any(r => r.ResourceEx.ContainsKey(2u)));

        main.Send(new CS_GunClassUpdate { GunId = id });
        var tooEarly = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunClassUpdate>().FirstOrDefault();
        Check("gun: a second promotion before reaching the new cap level is refused", tooEarly is null);

        main.Send(new CS_GunLevelUp { GunId = id, Level = 30 });
        var up30 = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunLevelUp>().FirstOrDefault();
        Check("gun: class 2 allows level 30", up30 is not null && up30.Level == 30);

        main.Send(new CS_GunLevelUp { GunId = id, Level = 1 });
        var down = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunLevelUp>().FirstOrDefault();
        Check("gun: lowering the level is refused", down is null);

        main.Send(new CS_GunLevelUp { GunId = 999999, Level = 10 });
        var unknown = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunLevelUp>().FirstOrDefault();
        Check("gun: unknown character is refused", unknown is null);
    }
}

if (scenario is "all" or "weapon")
{
    // Weapon progression: claim the starter kit (analysis blueprints), level up, lock, break with a duplicate, dismantle.
    main.Send(new CS_Mails());
    var wMails = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Mails>().FirstOrDefault();
    var kit = wMails?.TotalMails.FirstOrDefault(m => !m.Attached && m.AttachmentItems.ContainsKey(8301u));
    if (kit is not null)
    {
        main.Send(new CS_MailGetAttachment { Id = kit.Id });
        main.ReadFor(TimeSpan.FromSeconds(3));
        known45 += 10;
    }

    main.Send(new CS_GunWeapons());
    var weapons = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeapons>().FirstOrDefault();
    var w0 = weapons?.Weapons.FirstOrDefault();
    Check("weapon: the account has a weapon to level", w0 is not null);
    if (w0 is not null)
    {
        main.Send(new CS_GunWeaponLvUp { Id = w0.Id, Level = 10 });
        var wl = main.ReadFor(TimeSpan.FromSeconds(3));
        var wUp = wl.Select(p => p.Message).OfType<SC_GunWeaponUp>().FirstOrDefault();
        var wItems = wl.Select(p => p.Message).OfType<SC_Items>().FirstOrDefault();
        Check("weapon: level-up to 10 answered", wUp is not null && wUp.Level == 10 && wUp.Id == w0.Id, $"level={wUp?.Level}");
        Check("weapon: analysis blueprints (8301) were charged", wItems is not null && wItems.Items.ContainsKey(8301u) && wItems.Items[8301u] < 400000, $"left={wItems?.Items.GetValueOrDefault(8301u)}");

        main.Send(new CS_GunWeaponLockUnlock { Id = w0.Id });
        var lk = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunWeaponLockUnlock>().FirstOrDefault();
        Check("weapon: lock toggled on", lk is not null && lk.Locked);
        main.Send(new CS_GunWeaponLockUnlock { Id = w0.Id });
        var ul = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunWeaponLockUnlock>().FirstOrDefault();
        Check("weapon: lock toggled off", ul is not null && !ul.Locked);
    }

    // two identical loose weapons from the shop (goods 5002 = weapon 11016, 900 of item 47 each)
    var (_, wBefore) = CountOwned(main);
    for (int i = 0; i < 2; i++)
    {
        main.Send(new CS_StoreBuy { GoodsId = 5002, PriceType = 47 });
        main.ReadFor(TimeSpan.FromSeconds(3));
    }
    main.Send(new CS_GunWeapons());
    var after = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeapons>().FirstOrDefault();
    var loose = after?.Weapons.Where(w => w.StcId == 11016 && w.GunId == 0).OrderByDescending(w => w.Id).Take(2).ToList() ?? [];
    Check("weapon: two loose copies of weapon 11016 are available", loose.Count == 2);
    if (loose.Count == 2)
    {
        main.Send(new CS_GunWeaponBreak { Id = loose[0].Id, MaterialUid = loose[1].Id });
        var br = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeaponBreak>().FirstOrDefault();
        Check("weapon: break with a duplicate raises the break count", br is not null && br.BreakTimes == 2, $"times={br?.BreakTimes}");

        main.Send(new CS_GunWeaponDismantle { Uids = { loose[0].Id } });
        var dis = main.ReadFor(TimeSpan.FromSeconds(3));
        var disReply = dis.Select(p => p.Message).OfType<SC_GunWeaponDismantle>().FirstOrDefault();
        Check("weapon: dismantle removes the weapon and reports it", disReply is not null && disReply.Uids.Contains(loose[0].Id));
        Check("weapon: dismantle grants the blueprint product", dis.Any(p => p.Message is SC_Items it && it.Items.ContainsKey(12016u)), "item 12016 expected");

        main.Send(new CS_GunWeapons());
        var final = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeapons>().FirstOrDefault();
        Check("weapon: the dismantled weapon is gone for good", final is not null && final.Weapons.All(w => w.Id != loose[0].Id));
    }
}

if (scenario is "all" or "formation")
{
    // Custom squads are stored per account and come back in order of id.
    var squad = new CLHJOEOIFHP { Name = "Alpha" };
    squad.Guns.Add(new BattleAiGun { GunId = 1001, WeaponId = 7 });
    squad.Guns.Add(new BattleAiGun { GunId = 1008 });
    main.Send(new CS_CustomFormationModify { Id = 2, FOAHOCCEHMB = squad });
    var saved = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_CustomFormationModify>().FirstOrDefault();
    Check("formation: saving formation 2 echoes it", saved is not null && saved.Id == 2 && saved.FOAHOCCEHMB.Name == "Alpha" && saved.FOAHOCCEHMB.Guns.Count == 2);

    main.Send(new CS_CustomFormationModifyName { Id = 2, Name = "Bravo" });
    var renamed = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_CustomFormationModifyName>().FirstOrDefault();
    Check("formation: rename answered", renamed?.Name == "Bravo");

    main.Send(new CS_CustomFormationCopy { ENGKHJJNFGD = 2, GDJBIEKCEJC = { 4u } });
    var copied = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_CustomFormationCopy>().FirstOrDefault();
    Check("formation: copy to slot 4 answered with the copy", copied is not null && copied.FOAHOCCEHMB.ContainsKey(4u) && copied.FOAHOCCEHMB[4u].Name == "Bravo");

    main.Send(new CS_GetCustomFormation());
    var listing = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GetCustomFormation>().FirstOrDefault();
    var entries = listing?.OGNHICHKFAD?.KMPIHKFBAEP;
    Check("formation: the list is ordered by id with empty slots (1 empty, 2 and 4 saved, 3 empty)",
        entries is not null && entries.Count == 4 && entries[0].Guns.Count == 0 && entries[1].Name == "Bravo" && entries[1].Guns.Count == 2 && entries[2].Guns.Count == 0 && entries[3].Name == "Bravo",
        $"entries={entries?.Count}");
}

if (scenario == "stage")
{
    // Tutorial skip, stage data and lobby state (client 4.0.5136). Opt-in: not part of "all".
    static List<byte[]> StgFields(byte[] bytes, int number)
    {
        var found = new List<byte[]>();
        var reader = new CodedInputStream(bytes);
        uint tag;
        while ((tag = reader.ReadTag()) != 0)
        {
            if ((tag >> 3) == number && (tag & 7) == 2)
                found.Add(reader.ReadBytes().ToByteArray());
            else
                reader.SkipLastField();
        }
        return found;
    }

    static bool HasStageSkin(byte[] character)
    {
        var r = new CodedInputStream(character);
        uint t;
        while ((t = r.ReadTag()) != 0)
        {
            if ((t >> 3) == 5 && (t & 7) == 0)
                return r.ReadUInt32() != 0;
            r.SkipLastField();
        }
        return false;
    }

    main.Send(new CS_Sync());
    var stgSync = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Sync>().FirstOrDefault();
    Check("lobby: SC_Sync carries the game clock in SECONDS (login time + elapsed), not milliseconds (ms made the client date jump and reopen the daily notice popup)",
        stgSync is not null && stgSync.Timestamp >= 1779518860 && stgSync.Timestamp < 1779518860 + 3600, $"timestamp={stgSync?.Timestamp}");

    main.Send(new CS_SystemUnlocks());
    var stgUnlocks = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_SystemUnlocks>().FirstOrDefault();
    Check("lobby: the unlock list opens the systems of UnlockData beyond the captured list (60000, 44022) but not the unreleased ones (11011, 18000)",
        stgUnlocks?.SystemLock is not null && stgUnlocks.SystemLock.Unlocks.ContainsKey(60000) && stgUnlocks.SystemLock.Unlocks.ContainsKey(44022)
        && !stgUnlocks.SystemLock.Unlocks.ContainsKey(11011) && !stgUnlocks.SystemLock.Unlocks.ContainsKey(18000), $"count={stgUnlocks?.SystemLock?.Unlocks.Count}");

    main.Send(new CS_AdjutantSet { AdjutantId = 1017, CostumeId = 1101700 });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_BackAdjutantSet { Adjutants = { 1008u, 1024u } });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_AdjutantInfo());
    var stgAdj = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_AdjutantInfo>().FirstOrDefault()?.Adjutant;
    Check("lobby: the main-screen character and its side slots chosen with AdjutantSet/BackAdjutantSet are what AdjutantInfo returns afterwards",
        stgAdj is not null && stgAdj.Current == 1017 && stgAdj.Costume == 1101700 && stgAdj.Assistants.Count == 4 && stgAdj.Assistants[0] == 1008 && stgAdj.Assistants[1] == 1024 && stgAdj.Assistants[3] == 0,
        $"current={stgAdj?.Current} assistants=[{string.Join(",", stgAdj?.Assistants ?? new Google.Protobuf.Collections.RepeatedField<uint>())}]");

    main.Send(new CS_BackgroundChange { BackGround = 3001 });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_BackgroundInfo());
    var stgBg = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_BackgroundInfo>().FirstOrDefault();
    Check("lobby: BackgroundInfo returns the background chosen with BackgroundChange", stgBg is not null && stgBg.Background == 3001, $"background={stgBg?.Background}");

    main.Send(new CS_Guns());
    var stgOwned = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().FirstOrDefault();
    main.Send(new CS_LobbyGetGunSetting());
    var stgLobbyGuns = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_LobbyGetGunSetting>().FirstOrDefault();
    var stgOwnedIds = stgOwned?.Guns.Select(g => g.Id).ToHashSet() ?? [];
    Check("lobby: the lobby character roster (LobbyGetGunSetting) is exactly the characters of the account",
        stgLobbyGuns is not null && stgOwnedIds.Count > 0 && stgLobbyGuns.Guns.Select(g => g.GunId).ToHashSet().SetEquals(stgOwnedIds),
        $"owned={stgOwnedIds.Count} roster={stgLobbyGuns?.Guns.Count}");

    main.Send(new CS_StageRecord { Type = 33 });
    var stgRecords = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StageRecord>().FirstOrDefault();
    Check("stage: every StageRecord answer reports the opening stages (10101-10110, 10001...) as passed (so the client does not start the forced tutorial battles by itself)",
        stgRecords is not null && new uint[] { 10101, 10102, 10103, 10104, 10110, 10001 }.All(id => stgRecords.Records.TryGetValue(id, out var rec) && rec.FirstPassTime > 0));
    main.Send(new CS_TutorialInfo());
    var stgTutorial = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_TutorialInfo>().FirstOrDefault();
    Check("stage: TutorialInfo lists the first-battle guide groups (1010101, 1010121) as finished",
        stgTutorial?.TutorialInfo is not null && new uint[] { 1010101, 1010121 }.All(g => stgTutorial.TutorialInfo.Finished.TryGetValue(g, out var done) && done));
    main.Send(new CS_StageUsableGuns { StageId = 10104 });
    var stgUsable = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StageUsableGuns>().FirstOrDefault();
    Check("stage: StageUsableGuns for an ordinary stage is empty (no lent characters: the client uses the own ones of the account)", stgUsable is not null && stgUsable.Lineup.Count == 0 && stgUsable.LineupGuns.Count == 0);

    main.Send(new CS_StageStart { StcStageId = 10101 });
    var stgStart = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StageStart>().FirstOrDefault();
    Check("stage: StageStart for the tutorial answers with the battle", stgStart?.Stage is { StcStageId: 10101 });

    main.Send(new CS_StageInto { Guns = { [1769499u] = 1017u, [1835035u] = 1008u } });
    var stgInto = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StageInto>().FirstOrDefault();
    var stgBattle = stgInto?.Stage;
    Check("stage: the player is side 1 (Side and UserSides)", stgBattle is not null && (int)stgBattle.Side == 1 && stgBattle.UserSides.Count == 1 && stgBattle.UserSides.Values.All(v => (int)v == 1),
        $"side={(int?)stgBattle?.Side} userSides={stgBattle?.UserSides.Count}");
    var stgCamps = stgBattle?.Camps.Select(c => c.ToByteArray()).ToList() ?? [];
    int StgSide(byte[] camp) { var r = new CodedInputStream(camp); uint t = r.ReadTag(); return (t >> 3) == 1 ? r.ReadEnum() : 0; }
    var stgPlayerCamps = stgCamps.Where(c => StgSide(c) == 1).ToList();
    var stgUnits = stgPlayerCamps.SelectMany(c => StgFields(c, 2)).ToList();
    var stgEnemyUnits = stgCamps.Where(c => StgSide(c) != 1).SelectMany(c => StgFields(c, 2)).ToList();
    Check("stage: exactly one player camp (side 1) with the two starter characters and no enemy units (the client spawns the enemies; an empty enemy camp is allowed)",
        stgPlayerCamps.Count == 1 && stgUnits.Count == 2 && stgEnemyUnits.Count == 0, $"camps={stgCamps.Count} units={stgUnits.Count} enemyUnits={stgEnemyUnits.Count}");
    var stgGuns = stgUnits.Select(u => StgFields(u, 1).FirstOrDefault()).ToList();
    Check("stage: both units are Gun-case units with character, point id, skills and a stat map",
        stgGuns.Count == 2 && stgGuns.All(g => g is not null && StgFields(g, 1).Count == 1 && StgFields(g, 11).Count == 1 && StgFields(g, 22).Count == 1 && StgFields(g, 22)[0].Length > 10));
    // The client puts the first 5 skill ids in the main list and looks the rest up in BattleSkillData without a null check (crash of the 16:14 battle).
    List<uint> StgSkills(byte[] gun)
    {
        var ids = new List<uint>();
        foreach (var packed in StgFields(StgFields(gun, 11).FirstOrDefault() ?? [], 6))
        {
            var sr = new CodedInputStream(packed);
            while (!sr.IsAtEnd) ids.Add(sr.ReadUInt32());
        }
        return ids;
    }
    Check("stage: each unit lists the main skills of its grade (GunGradeData column 11, e.g. 10170101...) followed by its grade passives (column 5), never grade ids",
        stgGuns.Count == 2 && stgGuns.All(g => g is not null && StgSkills(g) is { Count: >= 4 } s && s.All(id => id > 1000000) && s.Distinct().Count() == s.Count),
        string.Join(" | ", stgGuns.Where(g => g is not null).Select(g => string.Join(",", StgSkills(g!)))));
    Check("stage: the character of each unit carries details_ with the weapon (field 30 with weapon 1 and weapon_lite 11) and a skin (field 5)",
        stgGuns.Count == 2 && stgGuns.All(g => g is not null && StgFields(g, 1).FirstOrDefault() is { } ch
            && StgFields(ch, 30).FirstOrDefault() is { } det && StgFields(det, 1).Count == 1 && StgFields(det, 11).Count == 1
            && HasStageSkin(ch)));
    var stgStats = new Dictionary<int, int>();
    if (stgGuns.FirstOrDefault() is { } stgFirst)
    {
        foreach (var entry in StgFields(StgFields(stgFirst, 22)[0], 1))
        {
            var er = new CodedInputStream(entry); int k = 0, v = 0; uint t2;
            while ((t2 = er.ReadTag()) != 0) { if ((t2 >> 3) == 1) k = er.ReadInt32(); else if ((t2 >> 3) == 2) v = er.ReadInt32(); else er.SkipLastField(); }
            stgStats[k] = v;
        }
    }
    // Keys are DevelopProperty values, not PropertyData columns (Pow = 1, MaxHp = 2, MaxAp = 6, CritMult = 10, FixedEffectBase = 77): with columns as keys
    // the attack landed in MaxHp and every hit did 0 damage.
    Check("stage: the stat map is keyed by DevelopProperty: attack (1) > 0, max hp (2) > attack, max ap (6) > 0, crit multiplier (10) = 1200, flag (77) = 1, nothing in max shield hp (3)",
        stgStats.GetValueOrDefault(1) > 0 && stgStats.GetValueOrDefault(2) > stgStats.GetValueOrDefault(1) && stgStats.GetValueOrDefault(6) > 0
        && stgStats.GetValueOrDefault(10) == 1200 && stgStats.GetValueOrDefault(77) == 1 && stgStats.GetValueOrDefault(3) == 0,
        string.Join(",", stgStats.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}")));
    // The 4.0.5136 client sends the result WITHOUT stage_progress: the stage of StageStart is the one that was played (the 20:17 log was answered
    // with a bare Lose and the result screen with its rewards never opened).
    main.Send(new CS_StageStart { StcStageId = 10145 });
    main.ReadFor(TimeSpan.FromSeconds(3));
    main.Send(new CS_StageResult { BattleResult = BattleResult.Win, Count = 1 });
    var stgResult = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StageResult>().FirstOrDefault();
    Check("stage: a Win result without stage_progress is resolved against the pending stage (SC_StageResult of 10145, Win)",
        stgResult is { StageId: 10145 } && stgResult.BattleResult == BattleResult.Win, $"stage={stgResult?.StageId} result={stgResult?.BattleResult}");
    // Every victory also gives the normal drops of the stage (26301: package 263011 = 1200 x 8301, always) in other_drop of the result.
    main.Send(new CS_StageStart { StcStageId = 26301 });
    main.ReadFor(TimeSpan.FromSeconds(3));
    main.Send(new CS_StageResult { BattleResult = BattleResult.Win, Count = 1 });
    var stgDrops = main.ReadFor(TimeSpan.FromSeconds(3));
    var stgDropResult = stgDrops.Select(p => p.Message).OfType<SC_StageResult>().FirstOrDefault();
    Check("stage: a victory lists its normal drops (26301: 1200 x 8301) and grants them",
        stgDropResult is not null && stgDropResult.OtherDrop.Any(d => d.ItemId == 8301 && d.ItemNum == 1200) && stgDrops.Any(p => p.Message is SC_Items i && i.Items.ContainsKey(8301u)),
        $"other={string.Join(",", stgDropResult?.OtherDrop.Select(d => $"{d.ItemId}x{d.ItemNum}") ?? [])}");
    // The real client announces stage_count chunks and the server asks for them with SC_StageResultNext; the report (type 1) carries the objectives
    // it met. complete_challenges of the record is a bitmask over StageData.challenge_list ([10101, 10701, 10208] for 26301).
    main.Send(new CS_StageStart { StcStageId = 26301 });
    main.ReadFor(TimeSpan.FromSeconds(3));
    main.Send(new CS_StageResult { BattleResult = BattleResult.Win, Count = 1, StageCount = 1 });
    var stgAsk = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StageResultNext>().FirstOrDefault();
    Check("stage: a result that announces stage_count chunks is answered with SC_StageResultNext asking for chunk 0 of type StageResult",
        stgAsk is { NextSeqId: 0, Type: StageResultType.StageResult }, $"next={stgAsk?.NextSeqId} type={stgAsk?.Type}");
    var stgReport = new CS_StageResult.Types.DGCMCOFLOJF { BattleResult = BattleResult.Win, StageProgress = new StageProgress { Challenges = { 10101u, 10208u, 99999u } } };
    main.Send(new CS_StageResultNext { SeqId = 0, Type = StageResultType.StageResult, Data = Google.Protobuf.ByteString.CopyFrom(Google.Protobuf.MessageExtensions.ToByteArray(stgReport)) });
    var stgStars = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).ToList();
    var stgRecord = stgStars.OfType<SC_StageRecordUpdate>().FirstOrDefault();
    Check("stage: the uploaded report marks objectives 1 and 3 (bits 0 and 2 = 5) in a record filed under the stage type (31); a made-up objective id counts for nothing",
        stgRecord is { Type: 31 } && stgRecord.Record.CompleteChallenges == 5 && stgStars.Any(m => m is SC_StageResult { StageId: 26301 }),
        $"type={stgRecord?.Type} mask={stgRecord?.Record?.CompleteChallenges}");
    // The result screen (AJCPLALCOAM) lights objective j when bit j is set in old_ccs OR new_ccs of SC_StageResult (fields 10/11) and plays the
    // "sweep" when it is only in new_ccs: both must carry the objective masks.
    var stgResultCcs = stgStars.OfType<SC_StageResult>().FirstOrDefault();
    Check("stage: SC_StageResult carries the objective masks (old_ccs 0, new_ccs 5) so the result screen lights the met objectives",
        stgResultCcs is { OldCcs: 0, NewCcs: 5 }, $"old={stgResultCcs?.OldCcs} new={stgResultCcs?.NewCcs}");
    main.Send(new CS_StageExit { State = EDDABKEADDC.StageExitStateLeave });
    main.ReadFor(TimeSpan.FromSeconds(2));
    // The stars of chapter 1 come from the records (bitmask popcount, opening stages keep their seeded objectives): the first chapter reward
    // (14 stars; item 12 x1, 1 x50, 25 x1000 in ChapterData column 29) must be GRANTED, not only acknowledged.
    main.Send(new CS_ChapterTakeReward { ChapterId = 1, RewardId = { ChapterRewardClass.RewardFirst } });
    var chapterClaim = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).ToList();
    Check("stage: the chapter star reward is granted with its items and confirmed (not just acknowledged)",
        chapterClaim.OfType<SC_ChapterTakeReward>().Any(c => c.RewardId.Contains(ChapterRewardClass.RewardFirst))
        && chapterClaim.Any(m => m is SC_Items or SC_ResourceUpdate or SC_Resource),
        string.Join(",", chapterClaim.Select(m => m.GetType().Name)));
}

if (scenario is "all" or "heat")
{
    // Lobby heat: interactions add heat (LobbyDailyHeatData), the day is capped at 100 and the week at 400, weekly rewards (LobbyWeeklyRewardData) need 80 heat per step.
    main.Send(new CS_LobbyGetQuestInfo());
    var heat0 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyGetQuestInfo>().FirstOrDefault();
    Check("heat: a new account starts with no heat", heat0 is not null && heat0.PAKHJDKPENP == 0 && heat0.LNJNKGNGBAC == 0, $"today={heat0?.PAKHJDKPENP} week={heat0?.LNJNKGNGBAC}");

    main.Send(new CS_LobbyGetWeeklyQuestReward { RewardId = 1 });
    Check("heat: a weekly reward is refused below its heat", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LobbyGetWeeklyQuestReward));

    main.Send(new CS_LobbyInteract { EKMDGLKBILM = 1023001, ECGAHCPCDDI = 603 });
    var heatMsgs = main.ReadFor(TimeSpan.FromSeconds(2));
    var heatUpdate = heatMsgs.Select(p => p.Message).OfType<SC_LobbyQuestHeatUpdate>().FirstOrDefault();
    Check("heat: an ASMR interaction gives 40 heat today and this week, and the first reward of the day is paid",
        heatUpdate is not null && heatUpdate.IHGKOKKEKDJ == 40 && heatUpdate.ELMAEDKKCGF == 40 && heatUpdate.NGCBPMPCMMJ && heatMsgs.Any(p => p.Message is SC_LobbyInteract) && heatMsgs.Any(p => p.Message is SC_ResourceUpdate or SC_Items),
        $"today={heatUpdate?.IHGKOKKEKDJ} week={heatUpdate?.ELMAEDKKCGF}");

    main.Send(new CS_LobbyInteract { EKMDGLKBILM = 1023001, ECGAHCPCDDI = 603 });
    Check("heat: the same interaction counts once a day", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LobbyQuestHeatUpdate));

    foreach (uint source in new uint[] { 604, 605, 601 })
    {
        main.Send(new CS_LobbyInteract { EKMDGLKBILM = 1023001, ECGAHCPCDDI = source });
        main.ReadFor(TimeSpan.FromSeconds(1));
    }

    main.Send(new CS_LobbyGetQuestInfo());
    var heat1 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyGetQuestInfo>().FirstOrDefault();
    Check("heat: the day is capped at 100 (40+40+40+20 would be 140)", heat1 is not null && heat1.PAKHJDKPENP == 100 && heat1.LNJNKGNGBAC == 100 && heat1.KCABBIEIGCJ.ContainsKey(603), $"today={heat1?.PAKHJDKPENP} week={heat1?.LNJNKGNGBAC}");

    main.Send(new CS_LobbyGetWeeklyQuestReward { RewardId = 1 });
    var weekly1 = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("heat: weekly reward 1 (80 heat) is paid", weekly1.Any(p => p.Message is SC_LobbyGetWeeklyQuestReward r && r.RewardId == 1) && weekly1.Any(p => p.Message is SC_ResourceUpdate or SC_Items));

    main.Send(new CS_LobbyGetWeeklyQuestReward { RewardId = 1 });
    Check("heat: weekly reward 1 cannot be claimed twice", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LobbyGetWeeklyQuestReward));

    main.Send(new CS_LobbyGetAllWeeklyQuestReward());
    Check("heat: claim-all with nothing left is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LobbyGetAllWeeklyQuestReward));

    main.Send(new CS_LobbyGetQuestInfo());
    var heat2 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyGetQuestInfo>().FirstOrDefault();
    Check("heat: QuestInfo lists the claimed weekly reward", heat2 is not null && heat2.GKPAAEMKODB.Contains(1u));

    // lobby events, mini games and expired items, requests the real client sent in the 2026-10-16 session
    main.Send(new CS_LobbyFinishEvent { EventId = 31411 });
    var finishEvent = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("lobby: finishing a lobby event pays its reward once and answers the event id",
        finishEvent.Any(p => p.Message is SC_LobbyFinishEvent e && e.EventId == 31411) && finishEvent.Any(p => p.Message is SC_Items or SC_ResourceUpdate));
    main.Send(new CS_LobbyFinishEvent { EventId = 31411 });
    var finishAgain = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("lobby: the same event pays nothing the second time the same day", finishAgain.Any(p => p.Message is SC_LobbyFinishEvent) && !finishAgain.Any(p => p.Message is SC_Items or SC_ResourceUpdate));

    main.Send(new CS_LobbyMiniGamePay { LAOKONCHCHL = 2, ObjectId = 2 });
    var payGame = main.ReadFor(TimeSpan.FromSeconds(2));
    var paid = payGame.Select(p => p.Message).OfType<SC_LobbyMiniGamePay>().FirstOrDefault();
    Check("lobby: a mini game is bought with the tokens earned from the heat (50) and reported unlocked, no -10018",
        paid is not null && paid.FJEINOBOEOI.MPNGHAKGLDM && payGame.Any(p => p.Message is SC_Items), string.Join(",", payGame.Select(p => p.Message.GetType().Name)));
    main.Send(new CS_LobbyMiniGamePay { LAOKONCHCHL = 2, ObjectId = 2 });
    var payAgain = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("lobby: buying it again charges nothing", payAgain.Any(p => p.Message is SC_LobbyMiniGamePay) && !payAgain.Any(p => p.Message is SC_Items));
    main.Send(new CS_LobbyMiniGameInfo());
    Check("lobby: the mini game list reports it unlocked", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyMiniGameInfo>().FirstOrDefault() is { } mg && mg.PDGDDBPGDKD[2].MPNGHAKGLDM);

    main.Send(new CS_ExGetThemesInfo());
    var themes0 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ExGetThemesInfo>().FirstOrDefault();
    Check("lobby: EX themes are announced, none picked yet", themes0 is not null && themes0.Info.LOJGCKOAEHP.Count >= 9 && themes0.Info.LOJGCKOAEHP.Values.All(v => !v.DDHPEIJCEME), $"themes={themes0?.Info.LOJGCKOAEHP.Count}");
    main.Send(new CS_ExPickThemeReward { ThemeId = 101 });
    var themePick = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("lobby: an EX theme reward is paid once", themePick.Any(p => p.Message is SC_ExPickThemeReward r && r.Rewards.Count > 0) && themePick.Any(p => p.Message is SC_Items or SC_ResourceUpdate));
    main.Send(new CS_ExPickThemeReward { ThemeId = 101 });
    Check("lobby: the same EX theme reward cannot be picked twice", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_ExPickThemeReward));
    main.Send(new CS_ExGetThemesInfo());
    Check("lobby: the picked EX theme is reported picked", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ExGetThemesInfo>().FirstOrDefault() is { } t1 && t1.Info.LOJGCKOAEHP[101].DDHPEIJCEME);

    var schemeEdit = new AFHDBEJPOHJ();
    schemeEdit.PKDLBOBNCBJ[102] = new AGGNMOGPNDI { PPKDIKDPBIG = { [10202u] = 999999u } };
    main.Send(new CS_LobbySetSchemeV2 { PNMFJCGGKNC = schemeEdit });
    Check("lobby: saving the layout is answered", main.ReadFor(TimeSpan.FromSeconds(2)).Any(p => p.Message is SC_LobbySetSchemeV2));
    main.Send(new CS_LobbyCustomizeRoomSetTheme { RoomId = 103, ThemeId = 77 });
    Check("lobby: setting a room theme is answered", main.ReadFor(TimeSpan.FromSeconds(2)).Any(p => p.Message is SC_LobbyCustomizeRoomSetTheme r && r.ThemeId == 77));
    main.Send(new CS_LobbyGetSchemeV2());
    var scheme = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyGetSchemeV2>().FirstOrDefault();
    Check("lobby: the saved layout survives (edited costume + theme) next to the default rooms",
        scheme is not null && scheme.CNLAIIIHBIK.PKDLBOBNCBJ.TryGetValue(102, out var r102) && r102.PPKDIKDPBIG[10202] == 999999
        && scheme.CNLAIIIHBIK.PKDLBOBNCBJ.TryGetValue(103, out var r103) && r103.ThemeId == 77 && scheme.CNLAIIIHBIK.PKDLBOBNCBJ[101].JOCPKDIKBPF.Count >= 3);

    main.Send(new CS_DeleteTimeLimitItems { Items = { 176902u, 177603u } });
    var delTime = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("lobby: expired items are deleted and echoed", delTime.Any(p => p.Message is SC_DeleteTimeLimitItems d && d.Items.Count == 2));
}

if (scenario is "all" or "modes")
{
    // Modes found by the probe sweep of 2026-10-16: the drone, intelligence files, mini game runs and closed modes.
    main.Send(new CS_UavRevelant());
    var uav0 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_UavRevelant>().FirstOrDefault();
    Check("modes: the drone is announced with its first arm in position 1 (an empty answer broke the UI)",
        uav0 is not null && uav0.UavRevelant.Uav.UavGrade == 1 && uav0.UavRevelant.Uav.UavEquips.Count == 3 && uav0.UavRevelant.Uav.UavEquips[0] == 7000 && uav0.UavRevelant.UavArms.ContainsKey(7000));
    main.Send(new CS_UnlockUavArm { ArmId = 7001 });
    Check("modes: a drone arm can be unlocked", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_UnlockUavArm>().FirstOrDefault() is { } ua && ua.Arm.StcId == 7001 && ua.Arm.Level == 1);
    main.Send(new CS_UavArmInstall { Position = 2, ArmId = 7001 });
    Check("modes: an arm is installed in a position", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_UavArmInstall>().FirstOrDefault() is { } inst && inst.UavEquips[1] == 7001 && inst.UavEquips[0] == 7000);
    main.Send(new CS_UavUpGrade());
    var uavGrade = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("modes: raising the drone grade without the materials is refused with the item error, not -10018",
        uavGrade.Any(p => p.Message is SC_ReportError e && e.ErrorNo != -10018) || uavGrade.Any(p => p.Message is SC_UavUpGrade));

    main.Send(new CS_UnlockCampFile { FileId = 1001001 });
    Check("modes: an intelligence file is unlocked", main.ReadFor(TimeSpan.FromSeconds(2)).Any(p => p.Message is SC_UnlockCampFile u && u.FileId == 1001001));
    main.Send(new CS_CampFiles());
    Check("modes: the unlocked file is listed, reward not taken", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_CampFiles>().FirstOrDefault() is { } cf && cf.CampFiles.TryGetValue(1001001, out bool takenYet) && !takenYet);
    main.Send(new CS_CampFileTakeRewards { FileId = 1001001 });
    var campReward = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("modes: the file reward is paid once", campReward.Any(p => p.Message is SC_CampFileTakeRewards) && campReward.Any(p => p.Message is SC_Items or SC_ResourceUpdate));
    main.Send(new CS_CampFileTakeRewards { FileId = 1001001 });
    Check("modes: the file reward is not paid twice", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_CampFileTakeRewards));

    // two connections of the same account claiming the same reward at the same time must be paid once (per-account lock)
    main.Send(new CS_UnlockCampFile { FileId = 1001002 });
    main.ReadFor(TimeSpan.FromSeconds(1));
    var parallel = await Connect(true);
    parallel.ReadFor(TimeSpan.FromSeconds(8));
    var racers = new[] { main, parallel };
    await Task.WhenAll(racers.Select(c => Task.Run(() => c.Send(new CS_CampFileTakeRewards { FileId = 1001002 }))));
    int paidTwice = racers.Sum(c => c.ReadFor(TimeSpan.FromSeconds(2)).Count(p => p.Message is SC_CampFileTakeRewards));
    parallel.Dispose();
    Check("modes: the same reward claimed at the same moment from two connections is paid once", paidTwice == 1, $"answers={paidTwice}");

    main.Send(new CS_LobbyMiniGameStart { LAOKONCHCHL = 2, StageId = 220 });
    var startRun = main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_LobbyMiniGameFinish { LAOKONCHCHL = 2, StageId = 220, Score = 123, Duration = 30 });
    var runPackets = main.ReadFor(TimeSpan.FromSeconds(2));
    var runDone = runPackets.Select(p => p.Message).OfType<SC_LobbyMiniGameFinish>().FirstOrDefault();
    bool startAccepted = startRun.Any(p => p.Message is SC_LobbyMiniGameStart);
    Check("modes: a mini game run reports score and best when its start was accepted, and a finish without a start is refused (never -10018)",
        startAccepted ? runDone is { Score: 123, CGFMBCCDFPG: 123 } : runDone is null && runPackets.Any(p => p.Message is SC_ReportError e && e.ErrorNo == -10008),
        $"started={startAccepted} best={runDone?.CGFMBCCDFPG}");

    main.Send(new CS_GachaTentimes { GachaId = 221001, Cost = { [271u] = 10u } });
    var skinPull = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GachaAcquirement>().FirstOrDefault();
    HashSet<uint> skinItems = [31095, 31107, 110067, 110068, 56020102, 23010102, 1530000, 1325174, 310054, 274];
    Check("modes: the skin gashapon (banner 221001) rolls ten outfit items from its own pool, not the captured weapons",
        skinPull is not null && skinPull.Drops.Count == 10 && skinPull.Drops.All(d => d.ItemId >= 200 && d.ItemId != 11030 && d.ItemId != 11048),
        string.Join(",", skinPull?.Drops.Select(d => d.ItemId) ?? []));

    main.Send(new CS_GunCostume { GunId = 1080, CostumeId = 1108001 });
    var wearCostume = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("modes: wearing a costume of an owned character (the request the real client sent 22 times and got refused)",
        wearCostume.Any(p => p.Message is SC_GunCostume c && c.GunId == 1080 && c.CostumeId == 1108001) || scenario != "all",
        string.Join(",", wearCostume.Select(p => p.Message is SC_ReportError e ? "error" + e.ErrorNo : p.Message.GetType().Name)));
    main.Send(new CS_DormSkinChange { GunId = 1055, CostumeId = 1105503 });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_DormSkinChange { GunId = 1055, CostumeId = 1105502 });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_LobbyGetGunSetting());
    var floorSetting = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyGetGunSetting>().FirstOrDefault();
    Check("modes: the outfit chosen in the activity floor is what LobbyGetGunSetting returns on re-entry",
        scenario != "all" || floorSetting?.Guns.FirstOrDefault(g => g.GunId == 1055)?.CostumeId == 1105502,
        $"costume={floorSetting?.Guns.FirstOrDefault(g => g.GunId == 1055)?.CostumeId}");
    main.Send(new CS_LobbyGunCostumeParts { GunId = 1021, CostumeId = 1102101 });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_LobbyGetGunSetting());
    var floorOnly = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyGetGunSetting>().FirstOrDefault();
    Check("modes: an outfit equipped in the floor with LobbyGunCostumeParts alone (the real request) is kept",
        scenario != "all" || floorOnly?.Guns.FirstOrDefault(g => g.GunId == 1021)?.CostumeId == 1102101,
        $"costume={floorOnly?.Guns.FirstOrDefault(g => g.GunId == 1021)?.CostumeId}");
    main.Send(new CS_LobbyAvatarSwitch { MPFFENCOPFP = 1085 });
    var switchGun = main.ReadFor(TimeSpan.FromSeconds(1)).Select(p => p.Message).OfType<SC_LobbyAvatarSwitch>().FirstOrDefault();
    main.Send(new CS_LobbyAvatarSwitch { MPFFENCOPFP = 10002, OCCAHBFCHNP = 1 });
    var switchNpc = main.ReadFor(TimeSpan.FromSeconds(1)).Select(p => p.Message).OfType<SC_LobbyAvatarSwitch>().FirstOrDefault();
    main.Send(new CS_LobbyAvatarInfo());
    var avatarNpc = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyAvatarInfo>().FirstOrDefault();
    Check("modes: the activity-floor avatar is kept with its TYPE (info field 3: character 0, NPC 1; an NPC typed as a character froze the loading at 87%)",
        switchGun is { MPFFENCOPFP: 1085, OCCAHBFCHNP: 0 } && switchNpc is { MPFFENCOPFP: 10002, OCCAHBFCHNP: 1 } && avatarNpc is { HIOGBPMJGON: { MPFFENCOPFP: 10002, AvatarType: 1 } });
    main.Send(new CS_LobbyAvatarSwitch { MPFFENCOPFP = 1085 });
    main.ReadFor(TimeSpan.FromSeconds(1));

    main.Send(new CS_HandmadeData());
    var handmade0 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_HandmadeData>().FirstOrDefault();
    Check("modes: the figurine collection score agrees with whether owned figurines are reported",
        handmade0 is not null && ((handmade0.HGMDLHCFPDE.NBONFIHFCBE.Count == 0 && handmade0.HGMDLHCFPDE.Score == 0)
            || (handmade0.HGMDLHCFPDE.NBONFIHFCBE.Count > 0 && handmade0.HGMDLHCFPDE.Score > 0)),
        $"score={handmade0?.HGMDLHCFPDE.Score} figurines={handmade0?.HGMDLHCFPDE.NBONFIHFCBE.Count}");
    main.Send(new CS_HandmadeClaimReward { RewardId = { 1u } });
    var handmadeClaim = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("modes: a figurine reward is refused below its score with a native error, never -10018",
        handmadeClaim.Any(p => p.Message is SC_ReportError e && e.ErrorNo != -10018) || handmadeClaim.Any(p => p.Message is SC_HandmadeClaimReward));

    main.Send(new CS_StoreTagRefresh { TagId = 201 });
    var storeRefresh = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("modes: a manual store refresh and a recharge reward get native refusals, not -10018",
        storeRefresh.Any(p => p.Message is SC_ReportError e && e.ErrorNo != -10018) && !storeRefresh.Any(p => p.Message is SC_ReportError { ErrorNo: -10018 }));
    main.Send(new CS_DarkZoneStart());
    var closedMode = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
    Check("modes: a closed mode answers with its own \"not open\" error instead of \"function locked\"", closedMode is not null && closedMode.ErrorNo != -10018 && closedMode.ErrorNo != 0, $"error={closedMode?.ErrorNo}");
}

if (scenario is "all" or "achievements")
{
    // Achievements are the UNLOCK CONDITIONS of chapters and activity stories (ChapterData column 16): without them chapter 2, the hard
    // chapters and every past-activity story stayed "未解锁" (real client, 2026-10-16).
    main.Send(new CS_QuestCount());
    var achQuest = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_QuestCount>().FirstOrDefault();
    var achCounters = achQuest?.Quest.Counters.FirstOrDefault(c => c.Type == CommonQuestCounters.Types.Type.Achievement);
    Check("achievements: SC_QuestCount reports the level ones (29301 theme activities, 10997 past activities) and 'normal 1-8 cleared' (99001, a seeded stage)",
        achCounters is not null && achCounters.Rewards.ContainsKey(29301) && achCounters.Rewards.ContainsKey(10997) && achCounters.Rewards.ContainsKey(99001),
        $"reported={achCounters?.Rewards.Count}");
    Check("achievements: an owned character completes its level ladder (人形升至N级, ids 40GGNN): the talent tree and Mind Spiral ask them",
        achCounters is not null && achCounters.Rewards.Keys.Any(k => k is >= 400000 and <= 499999), $"reported={achCounters?.Rewards.Count}");
    // plot-only stages (StageData col 21 = 3, e.g. SL-2-1 = 10281): the real client sends nothing after the dialogue, so starting it passes it (A-63)
    main.Send(new CS_StageStart { StcStageId = 10281 });
    var plotStart = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).ToList();
    Check("plot stage: starting a dialogue-only stage passes it at once (SC_StageRecordUpdate with a first-pass time), no battle start and no result screen",
        !plotStart.OfType<SC_StageStart>().Any() && plotStart.OfType<SC_StageRecordUpdate>().Any(u => u.Record.StageId == 10281 && u.Record.FirstPassTime != 0)
        && !plotStart.OfType<SC_StageResult>().Any(), $"got={string.Join(",", plotStart.Select(m => m.GetType().Name))}");
    Check("plot stage: the batch record update (what makes the chapter map redraw its ticks and unlocks) is sent too",
        plotStart.OfType<SC_StageRecordUpdateBatch>().Any(b => b.Record.Any(r => r.StageId == 10281)));
    // the chapter map is hidden while the plot plays: the record comes again after the next requests of the client (A-64)
    main.Send(new CS_Sync());
    var plotAgain = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).ToList();
    Check("plot stage: the record is sent again after the client's next request (the map redraws the tick and the unlock when it is back)",
        plotAgain.OfType<SC_StageRecordUpdateBatch>().Any(b => b.Record.Any(r => r.StageId == 10281)) && plotAgain.OfType<SC_StageRecordUpdate>().Any(u => u.Record.StageId == 10281),
        $"got={string.Join(",", plotAgain.Select(m => m.GetType().Name))}");
    // the chapter screen rebuilds its map (ticks, unlocks) when it comes back only after the client's event 1105, raised by SC_ChapterHeroMode (A-69)
    Check("plot stage: the chapter-changed notice (SC_ChapterHeroMode, empty) is sent with the record, and again later",
        plotStart.OfType<SC_ChapterHeroMode>().Any(h => h.ChapterId.Count == 0) && plotAgain.OfType<SC_ChapterHeroMode>().Any(),
        $"start={string.Join(",", plotStart.Select(m => m.GetType().Name))} again={string.Join(",", plotAgain.Select(m => m.GetType().Name))}");
    main.Send(new CS_StageRecord { Type = 11 });
    Check("plot stage: the passed plot stage is in the stage records",
        main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_StageRecord>().FirstOrDefault()?.Records.ContainsKey(10281) == true);

    // the command-center backgrounds (all scenes and 3D skins) are unlocked by the achievement of CommandBackgroundData column 7 (A-62)
    Check("achievements: the unlock ids of the command-center backgrounds (43001..43006 the 3D scenes, 44023, 70000, 71000) are all complete",
        achCounters is not null && new uint[] { 43001, 43002, 43003, 43004, 43005, 43006, 44023, 70000, 71000 }.All(achCounters.Rewards.ContainsKey));
    Check("achievements: 'normal SL-2-5 cleared' (99002) is not complete before that stage is cleared", achCounters is not null && !achCounters.Rewards.ContainsKey(99002));
    // SL-2-5 is a plot-only stage: starting it passes it (A-63), so the achievement update comes with the start (a battle stage would send it with the result)
    main.Send(new CS_StageStart { StcStageId = 10285 });
    var achClear = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).ToList();
    main.Send(new CS_StageResult { BattleResult = BattleResult.Win, Count = 1 });
    achClear.AddRange(main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message));
    var achUpdate = achClear.OfType<SC_CommonQuestCountUpdate>().FirstOrDefault(u => u.Counters.Type == CommonQuestCounters.Types.Type.Achievement);
    Check("achievements: clearing SL-2-5 completes 99002 and the client is told at once (SC_CommonQuestCountUpdate)",
        achUpdate is not null && achUpdate.Counters.Rewards.ContainsKey(99002), $"update={achUpdate?.Counters.Rewards.Count}");
    var claimable = achCounters?.Rewards.Where(r => !r.Value).Select(r => r.Key).FirstOrDefault() ?? 0u;
    if (claimable != 0)
    {
        main.Send(new CS_CommonQuestTakeReward { Type = CommonQuestCounters.Types.Type.Achievement, QuestId = { claimable } });
        var achTake = main.ReadFor(TimeSpan.FromSeconds(2));
        Check("achievements: the reward of a complete achievement can be taken once", achTake.Any(p => p.Message is SC_CommonQuestTakeReward { Type: CommonQuestCounters.Types.Type.Achievement } r && r.QuestId.Contains(claimable)));
        main.Send(new CS_CommonQuestTakeReward { Type = CommonQuestCounters.Types.Type.Achievement, QuestId = { claimable } });
        var achAgain = main.ReadFor(TimeSpan.FromSeconds(2));
        Check("achievements: the same reward is refused the second time with a native error", !achAgain.Any(p => p.Message is SC_CommonQuestTakeReward) && achAgain.Any(p => p.Message is SC_ReportError e && e.ErrorNo != -10018));
    }
}

if (scenario is "all" or "recordroom")
{
    // Archive center (opt-in with seeded items for the paid part: seed-items.py 8119=5; in "all" only the refusal without the item is checked).
    main.Send(new CS_RecordRoom());
    Check("recordroom: a new account has no archive details", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RecordRoom>().FirstOrDefault() is { } rr0 && rr0.Storys.Count == 0 || scenario == "recordroom");
    main.Send(new CS_RecordRoomUnLockDetail { StoryId = 101, DetailId = 10102 });
    Check("recordroom: detail 2 cannot be unlocked before detail 1", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_RecordRoomUnLockDetail));
    main.Send(new CS_RecordRoomUnLockDetail { StoryId = 101, DetailId = 10101 });
    var unlock1 = main.ReadFor(TimeSpan.FromSeconds(2));
    if (scenario == "recordroom")
    {
        Check("recordroom: detail 1 is unlocked with the archive item", unlock1.Any(p => p.Message is SC_RecordRoomUnLockDetail u && u.DetailId == 10101));
        main.Send(new CS_RecordRoomGetDetailReward { StoryId = 101 });
        var rrReward = main.ReadFor(TimeSpan.FromSeconds(2));
        Check("recordroom: the reward of the unlocked detail is paid once", rrReward.Any(p => p.Message is SC_RecordRoomGetDetailReward) && rrReward.Any(p => p.Message is SC_Items or SC_ResourceUpdate));
        main.Send(new CS_RecordRoomGetDetailReward { StoryId = 101 });
        Check("recordroom: and not twice", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_RecordRoomGetDetailReward));
        main.Send(new CS_RecordRoom());
        Check("recordroom: the state is listed", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RecordRoom>().FirstOrDefault() is { } rr1 && rr1.Storys.TryGetValue(101, out var d) && d.DetailIdx == 1 && d.State);
    }
    else
    {
        Check("recordroom: without the archive item the unlock is refused with the item error, not -10018",
            unlock1.Any(p => p.Message is SC_ReportError e && e.ErrorNo != -10018) || unlock1.Any(p => p.Message is SC_RecordRoomUnLockDetail));
    }
}

if (scenario == "love")
{
    // Affection (needs gifts the account does not have: run seed-items.py first, so it is not part of "all"): gifts (item 300040 = 60 love experience each), level thresholds of LoveLevelData.
    main.Send(new CS_Guns());
    var loveGuns = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().FirstOrDefault();
    uint loveGun = loveGuns?.Guns.FirstOrDefault()?.Id ?? 0;
    main.Send(new CS_LoveSendGift { GunId = loveGun, MLDDFFDLNHD = { [300040u] = 5u } });
    var gift1 = main.ReadFor(TimeSpan.FromSeconds(2));
    var love1 = gift1.Select(p => p.Message).OfType<SC_LoveSendGift>().FirstOrDefault();
    // item 300040 is worth 60; character 1001 has it in the first list of LoveGiftSpecialData (+75 %: the client shows 105 per gift on the bar), A-57
    Check("love: 5 gifts give 525 experience (60 x 1.75 preferred-gift bonus x 5, as the client previews) and stay on level 1",
        love1 is not null && love1.JLHFICPMAEI == 1 && love1.JKGBIKEDACK == (loveGun == 1001 ? 525 : love1.JKGBIKEDACK) && love1.JKGBIKEDACK >= 300 && gift1.Any(p => p.Message is SC_Items),
        $"gun={loveGun} level={love1?.JLHFICPMAEI} exp={love1?.JKGBIKEDACK}");

    Check("love: the 64-bit field of the answer carries the experience (the screen showed the clock time \"1792136112/840\" when it carried the time)",
        love1 is not null && love1.FDKIHAPHHPD == 0 && love1.JKGBIKEDACK < 100000, $"long={love1?.JKGBIKEDACK} pending={love1?.FDKIHAPHHPD} (the second 32-bit field is the pending event: only non-zero when the bar of the level is full)");
    if (loveGun == 1008)
    {
        // character 1008 lists item 300014 (worth 100) in the first list of LoveGiftSpecialData: +75 % = 175 (needs seed 300014=10)
        main.Send(new CS_LoveSendGift { GunId = loveGun, MLDDFFDLNHD = { [300014u] = 1u } });
        var preferred = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LoveSendGift>().FirstOrDefault();
        Check("love: a preferred gift gives its value x1.75 (100 -> 175), as the client previews", preferred is not null && preferred.JKGBIKEDACK == (love1?.JKGBIKEDACK ?? 0) + 175, $"exp={preferred?.JKGBIKEDACK} before={love1?.JKGBIKEDACK}");
    }

    main.Send(new CS_LoveSendGift { GunId = loveGun, MLDDFFDLNHD = { [300040u] = 10u } });
    var love2Packets = main.ReadFor(TimeSpan.FromSeconds(2));
    var love2 = love2Packets.Select(p => p.Message).OfType<SC_LoveSendGift>().FirstOrDefault();
    // A-55: a full bar does NOT raise the level; the client shows the event of the level (LoveLevelData col 3 = 101) and the level rises when it is done
    Check("love: the full bar (840) keeps level 1 and announces the pending event 101 (story/demand button of the client)",
        love2 is not null && love2.JLHFICPMAEI == 1 && love2.JKGBIKEDACK == 840 && love2.FDKIHAPHHPD == 101, $"level={love2?.JLHFICPMAEI} exp={love2?.JKGBIKEDACK} pending={love2?.FDKIHAPHHPD}");
    main.Send(new CS_LoveSendGift { GunId = loveGun, MLDDFFDLNHD = { [300040u] = 1u } });
    Check("love: gifts are refused while the event is pending", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LoveSendGift));
    main.Send(new CS_LoveSendEventGift { GunId = loveGun });
    var event1 = main.ReadFor(TimeSpan.FromSeconds(3));
    var eventDone = event1.Select(p => p.Message).OfType<SC_LoveSendEventGift>().FirstOrDefault();
    Check("love: completing the event raises the level to 2 (client order: field 2 = pending, field 3 = level)",
        eventDone is not null && eventDone.JLHFICPMAEI == 2 && eventDone.FDKIHAPHHPD == 0, $"level={eventDone?.JLHFICPMAEI} pending={eventDone?.FDKIHAPHHPD}");
    Check("love: reaching level 2 pays its reward (LoveLevelData col 7: a stored-stamina ticket, item 8)",
        event1.Select(p => p.Message).OfType<SC_Items>().Any(i => i.Items.ContainsKey(8u))
        || event1.Select(p => p.Message).OfType<SC_ResourceUpdate>().Any(r => r.ResourceEx.ContainsKey(8u)));
    main.Send(new CS_LoveSendEventGift { GunId = loveGun });
    Check("love: with no event pending the request is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LoveSendEventGift));
    main.Send(new CS_LoveUpgradeDirectly { GunId = loveGun });
    var direct = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LoveUpgradeDirectly>().FirstOrDefault();
    Check("love: upgrading directly completes the event of the level (level 3)", direct is not null && direct.JLHFICPMAEI == 3 && direct.FDKIHAPHHPD == 0, $"level={direct?.JLHFICPMAEI} pending={direct?.FDKIHAPHHPD}");

    main.Send(new CS_LoveSendGift { GunId = loveGun, MLDDFFDLNHD = { [999999u] = 1u } });
    Check("love: a non-gift item is refused without an answer", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LoveSendGift));

    main.Send(new CS_LoveVow { GunId = loveGun });
    Check("love: the vow is refused below the maximum love", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LoveVow));
}

if (scenario == "items")
{
    // Backpack (needs materials a new account does not have: seed-items.py 4021=3 9006=6 133001=2 121010=2): sell, compose, use.
    main.Send(new CS_ItemDismantle { IdAndNum = { [4021u] = 3u } });
    var sold = main.ReadFor(TimeSpan.FromSeconds(2));
    var soldGold = sold.Select(p => p.Message).OfType<SC_ResourceUpdate>().FirstOrDefault();
    Check("items: selling 3 character fragments pays 15000 gold and empties the stack",
        sold.Any(p => p.Message is SC_ItemDismantle) && soldGold is not null && soldGold.ResourceEx.TryGetValue(2u, out int goldAfter) && goldAfter >= 15000
        && sold.Select(p => p.Message).OfType<SC_Items>().Any(i => i.Items.TryGetValue(4021u, out int left) && left == 0));

    main.Send(new CS_ItemDismantle { IdAndNum = { [4021u] = 1u } });
    Check("items: selling what is not owned is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_ItemDismantle));

    main.Send(new CS_ComposeItems { ItemId = 9007, ItemNum = 2 });
    var composed = main.ReadFor(TimeSpan.FromSeconds(2));
    var composedItems = composed.Select(p => p.Message).OfType<SC_Items>().ToList();
    Check("items: composing 2 x 9007 spends 6 x 9006 and grants 2",
        composed.Any(p => p.Message is SC_ComposeItems) && composedItems.Any(i => i.Items.TryGetValue(9006u, out int a) && a == 0) && composedItems.Any(i => i.Items.TryGetValue(9007u, out int b) && b == 2));

    main.Send(new CS_ComposeItems { ItemId = 9007, ItemNum = 1 });
    Check("items: composing without materials is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_ComposeItems));

    main.Send(new CS_UseItems { ItemId = 133001, Num = 2 });
    var packUse = main.ReadFor(TimeSpan.FromSeconds(2));
    var packRes = packUse.Select(p => p.Message).OfType<SC_ResourceUpdate>().ToList();
    Check("items: using 2 packs grants their contents (16000 battle reports, 16000 weapon EXP)",
        packUse.Any(p => p.Message is SC_UseItems) && packRes.Any(r => r.ResourceEx.ContainsKey(25u)) && packUse.Select(p => p.Message).OfType<SC_Items>().Any(i => i.Items.ContainsKey(8301u)));

    main.Send(new CS_UseItems { ItemId = 121010, Num = 2 });
    var boxUse = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("items: opening 2 boxes grants 2 drops from their package", boxUse.Any(p => p.Message is SC_UseItems) && boxUse.Select(p => p.Message).OfType<SC_Items>().Any());

    main.Send(new CS_UseItems { ItemId = 8301, Num = 1 });
    Check("items: an item with no use is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_UseItems));

    // weapon synthesis (seed-items.py 12036=60): 60 blueprints make weapon 11036
    main.Send(new CS_WeaponCompose { WeaponAndNum = { [11036u] = 1u } });
    var weaponMade = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("items: 60 blueprints compose weapon 11036 (new weapon + blueprints spent)",
        weaponMade.Any(p => p.Message is SC_WeaponCompose) && weaponMade.Any(p => p.Message is SC_NewGunWeapon nw && nw.Weapon.StcId == 11036)
        && weaponMade.Select(p => p.Message).OfType<SC_Items>().Any(i => i.Items.TryGetValue(12036u, out int left) && left == 0));

    main.Send(new CS_WeaponCompose { WeaponAndNum = { [11036u] = 1u } });
    Check("items: composing again without blueprints is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_WeaponCompose));
}

if (scenario == "cumulative")
{
    // Cumulative pull rewards (needs 60 permits: seed-items.py 45=200:1): 60 pulls on banner 240001 unlock the first reward of group 1001.
    main.Send(new CS_GetGachaCumulativeInfo());
    var cumBefore = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GetGachaCumulativeInfo>().FirstOrDefault();
    Check("cumulative: group 1001 starts with no pulls and no claimed rewards",
        cumBefore is not null && cumBefore.EMFOHMKKMKA.TryGetValue(1001u, out var cb) && cb.GachaTimes == 0 && cb.Rewards.Count == 0);

    main.Send(new CS_GachaCumulativeGetReward { RewardIds = { 1u } });
    Check("cumulative: the 60-pull reward is refused before the pulls", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_GachaCumulativeGetReward));

    for (int i = 0; i < 6; i++)
    {
        main.Send(new CS_GachaTentimes { GachaId = 240001 });
        main.ReadFor(TimeSpan.FromSeconds(6));
    }

    main.Send(new CS_GetGachaCumulativeInfo());
    var cumMid = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GetGachaCumulativeInfo>().FirstOrDefault();
    Check("cumulative: 60 pulls are counted for the group", cumMid is not null && cumMid.EMFOHMKKMKA.TryGetValue(1001u, out var cm) && cm.GachaTimes == 60, $"times={cumMid?.EMFOHMKKMKA.GetValueOrDefault(1001u)?.GachaTimes}");

    main.Send(new CS_GachaCumulativeGetReward { RewardIds = { 1u, 2u } });
    var cumClaim = main.ReadFor(TimeSpan.FromSeconds(2));
    var cumReply = cumClaim.Select(p => p.Message).OfType<SC_GachaCumulativeGetReward>().FirstOrDefault();
    Check("cumulative: reward 1 (60 pulls) is granted and reward 2 (120 pulls) is not",
        cumReply is not null && cumReply.RewardIds.Count == 1 && cumReply.RewardIds[0] == 1 && cumClaim.Any(p => p.Message is SC_ResourceUpdate or SC_Items or SC_NewGunWeapon));

    main.Send(new CS_GachaCumulativeGetReward { RewardIds = { 1u } });
    Check("cumulative: a reward is only granted once", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_GachaCumulativeGetReward));
}

if (scenario == "controlroom")
{
    // Control room (opt-in, not part of "all": the server must run with GRIFFIN_CONTROLROOM_TIME_SCALE=100000 so a 20 h dispatch ends at once): dispatch, settle, milestones, upgrade.
    main.Send(new CS_Guns());
    var roomGuns = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().FirstOrDefault()?.Guns.Select(g => g.Id).Take(2).ToList() ?? [];
    var dispatchRequest = new CS_ControlRoomDispatch { BGDEDEFLJJO = 1001 };
    dispatchRequest.GGOLBAILKIO.AddRange(roomGuns);
    main.Send(dispatchRequest);
    var dispatched = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ControlRoomDispatch>().FirstOrDefault();
    Check("controlroom: dispatching two characters to place 1001 starts the clock", dispatched?.HLIHNOCKGLD is { StartTime: > 0 } d && d.GGOLBAILKIO.Count == roomGuns.Count && roomGuns.Count == 2);

    main.Send(dispatchRequest);
    Check("controlroom: the same place cannot be dispatched twice", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_ControlRoomDispatch));

    main.Send(new CS_ControlRoomInfo());
    var roomInfo = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ControlRoomInfo>().FirstOrDefault();
    Check("controlroom: the info lists the running dispatch", roomInfo?.LIGIJKFNABP?.BCFPLLOKLOJ.ContainsKey(1001u) == true && roomInfo.LIGIJKFNABP.Level == 1);
    long gameClock = login.Select(p => p.Message).OfType<SC_Sync>().FirstOrDefault()?.Timestamp ?? 0;
    Check("controlroom: the timestamps are in the client's clock (SC_Sync), not the real one: the stamina purchase dialog crashed the client with negative elapsed times",
        gameClock > 0 && Math.Abs(roomInfo?.LIGIJKFNABP?.DFABABLCGHH?.RefreshTime - gameClock ?? long.MaxValue) < 3600
        && roomInfo!.LIGIJKFNABP.BCFPLLOKLOJ[1001u].StartTime is var dispatchStart && Math.Abs(dispatchStart - gameClock) < 3600,
        $"clock={gameClock} stamina={roomInfo?.LIGIJKFNABP?.DFABABLCGHH?.RefreshTime}");

    main.Send(new CS_ControlRoomDispatchSettle { HABGIDKHDMP = { 1001u } });
    var settled = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("controlroom: a finished dispatch pays its reward (4000 gold or the place item) and is closed",
        settled.Any(p => p.Message is SC_ControlRoomDispatchSettle) && settled.Any(p => p.Message is SC_ResourceUpdate or SC_Items));

    main.Send(new CS_ControlRoomDispatchSettle { HABGIDKHDMP = { 1001u } });
    Check("controlroom: settling twice grants nothing", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_ControlRoomDispatchSettle));

    main.Send(new CS_ControlRoomDispatchReward { RewardIds = { 2u } });
    Check("controlroom: the 10-dispatch milestone is refused after one dispatch", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_ControlRoomDispatchReward));

    main.Send(new CS_ControlRoomUpgrade());
    Check("controlroom: upgrading without the room materials is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_ControlRoomUpgrade));

    // material production (time scaled): choose gold, wait, withdraw
    main.Send(new CS_ControlRoomSetGainItem { ItemId = 12345 });
    Check("controlroom: an item the room cannot produce is refused (ControlRoomGainItemLocked)",
        main.ReadFor(TimeSpan.FromSeconds(1)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo == -14299));
    main.Send(new CS_ControlRoomSetGainItem { ItemId = 2 });
    var gainSet = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ControlRoomSetGainItem>().FirstOrDefault();
    Check("controlroom: choosing gold starts an empty store", gainSet is not null && gainSet.OIMEBMHAFPE.ContainsKey(2u));
    Thread.Sleep(2500);
    main.Send(new CS_ControlRoomInfo());
    var gainInfo = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ControlRoomInfo>().FirstOrDefault();
    long stored = gainInfo?.LIGIJKFNABP?.OIMEBMHAFPE.GetValueOrDefault(2u)?.JEELHBDIIHI ?? 0;
    Check("controlroom: the room has produced gold while waiting (capped by the storage)", stored > 0, $"stored={stored}");
    main.Send(new CS_ControlRoomWithdrawGainItem { ItemId = 2, Num = 1 });
    var gainWithdraw = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("controlroom: withdrawing 1 gold pays it", gainWithdraw.Any(p => p.Message is SC_ControlRoomWithdrawGainItem) && gainWithdraw.Any(p => p.Message is SC_ResourceUpdate));
    main.Send(new CS_ControlRoomWithdrawGainItem { ItemId = 2, Num = 100000000 });
    Check("controlroom: withdrawing more than is stored is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_ControlRoomWithdrawGainItem));

    // stamina production (ControlRoomData col 4 per minute, time scaled): the info reports it and WithdrawStamina moves it to the commander
    long staminaStored = gainInfo?.LIGIJKFNABP?.DFABABLCGHH?.JEELHBDIIHI ?? 0;
    main.Send(new CS_ControlRoomInfo());
    staminaStored = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ControlRoomInfo>().FirstOrDefault()?.LIGIJKFNABP?.DFABABLCGHH?.JEELHBDIIHI ?? 0;
    Check("controlroom: the room has produced stamina while waiting", staminaStored > 0, $"stamina={staminaStored}");
    main.Send(new CS_ControlRoomWithdrawStamina { Num = 1 });
    var staminaOut = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("controlroom: WithdrawStamina answers and updates the commander's stamina",
        staminaOut.Any(p => p.Message is SC_ControlRoomWithdrawStamina) && staminaOut.Any(p => p.MsgId == MsgId.MsgScStaminaTypeResourceUpdate));
}

if (scenario == "mods")
{
    // Weapon mods (opt-in: /inventory addall weaponmod fills the account with every mod at level 0): lock, level up with a material, dismantle.
    main.Send(new CS_Chat { Uid = 1, Message = "/inventory addall weaponmod" });
    main.ReadFor(TimeSpan.FromSeconds(8));
    main.Send(new CS_GunWeaponMods());
    var allMods = main.ReadFor(TimeSpan.FromSeconds(8)).Select(p => p.Message).OfType<SC_GunWeaponMods>().SelectMany(m => m.Mods).ToList();
    var modA = allMods.FirstOrDefault(m => m.StcId == 30141121);
    var modB = allMods.FirstOrDefault(m => m.StcId == 120123121);
    var modC = allMods.FirstOrDefault(m => m.StcId == 30146343);
    Check("mods: the account owns the mods used by this test", modA is not null && modB is not null && modC is not null, $"mods={allMods.Count}");
    if (modA is not null && modB is not null && modC is not null)
    {
        main.Send(new CS_GunWeaponModLockUnlock { Id = modB.Id });
        var locked = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunWeaponModLockUnlock>().FirstOrDefault();
        Check("mods: locking a mod answers locked=true", locked is { Locked: true } && locked.Id == modB.Id);

        var lvUp = new CS_GunWeaponModLvUp { Id = modA.Id, Materials = { modB.Id } };
        main.Send(lvUp);
        Check("mods: a locked mod cannot be used as a material", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_GunWeaponModLvUp));

        main.Send(new CS_GunWeaponModLockUnlock { Id = modB.Id });
        var unlocked = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunWeaponModLockUnlock>().FirstOrDefault();
        Check("mods: unlocking answers locked=false", unlocked is { Locked: false });

        main.Send(lvUp);
        var up = main.ReadFor(TimeSpan.FromSeconds(2));
        var upReply = up.Select(p => p.Message).OfType<SC_GunWeaponModLvUp>().FirstOrDefault();
        Check("mods: feeding a 200-experience mod to a rank-2 mod raises its level and charges gold",
            upReply is not null && upReply.Level > 1 && up.Any(p => p.Message is SC_ResourceUpdate), $"level={upReply?.Level} exp={upReply?.Exp}");

        main.Send(new CS_GunWeaponMods());
        var after = main.ReadFor(TimeSpan.FromSeconds(4)).Select(p => p.Message).OfType<SC_GunWeaponMods>().SelectMany(m => m.Mods).ToList();
        Check("mods: the material was consumed", after.All(m => m.Id != modB.Id) && after.Any(m => m.Id == modA.Id && m.Level == upReply?.Level));

        main.Send(new CS_GunWeaponModDismantle { Ids = { modC.Id } });
        var dis = main.ReadFor(TimeSpan.FromSeconds(2));
        Check("mods: dismantling a mod returns upgrade items (8307) and lists the removed id",
            dis.Select(p => p.Message).OfType<SC_GunWeaponModDismantle>().Any(r => r.Ids.Contains(modC.Id)) && dis.Select(p => p.Message).OfType<SC_Items>().Any(i => i.Items.ContainsKey(8307u)));

        // lock plans: saved selections per plan group and slot
        var plan = new CS_SetGunWeaponModLockPlan { BEFHELAINLN = 7, ODMAMCEEMBD = 2, IFKCJEAGCFH = 1 };
        plan.CEAEBKMHDCJ.Add(modA.Id);
        main.Send(plan);
        var planReply = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SetGunWeaponModLockPlan>().FirstOrDefault();
        Check("mods: a lock plan is saved and returned by slot",
            planReply?.CLMMDIGBOJC is { BEFHELAINLN: 7 } info && info.LCPMEOFCFPB.TryGetValue(2u, out var saved) && saved.CEAEBKMHDCJ.Contains(modA.Id) && saved.IFKCJEAGCFH == 1);
        main.Send(new CS_DelGunWeaponModLockPlan { BEFHELAINLN = 7, ODMAMCEEMBD = 2 });
        Check("mods: deleting the slot answers with the plan without it", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_DelGunWeaponModLockPlan>().Any(r => r.CLMMDIGBOJC.LCPMEOFCFPB.Count == 0));
        main.Send(new CS_DelGunWeaponModLockPlan { BEFHELAINLN = 7, ODMAMCEEMBD = 2 });
        Check("mods: deleting a slot that does not exist is refused", main.ReadFor(TimeSpan.FromSeconds(1)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo == -10426));
        main.Send(new CS_GunWeaponModLockUnlock { Id = modA.Id });
        main.ReadFor(TimeSpan.FromSeconds(1));
        main.Send(new CS_UnlockGunWeaponModLockPlan { JNADCNCACDA = { modA.Id } });
        Check("mods: unlock-by-plan frees the mod", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_UnlockGunWeaponModLockPlan>().Any(r => r.Ids.Contains(modA.Id) && !r.Locked));
    }
}

if (scenario is "all" or "locked")
{
    // A player action the server does not implement and no family rule covers (the developer login check) is answered with the client's "system locked" error, not silence.
    main.Send(new CS_DevIsLogin());
    var lockedMessages = main.ReadFor(TimeSpan.FromSeconds(2));
    var lockedError = lockedMessages.Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
    Check("locked: an unimplemented player action is refused with UserSystemLocked (-10018) for that request",
        lockedError is not null && lockedError.ErrorNo == -10018 && lockedError.ProtocolId == (uint)MsgId.MsgCsDevIsLogin && !lockedMessages.Any(p => p.Message is SC_DevIsLogin));
}

if (scenario is "all" or "display")
{
    // Characters shown on the player card.
    main.Send(new CS_Guns());
    var shownGuns = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().FirstOrDefault()?.Guns.Select(g => g.Id).Take(2).ToList() ?? [];
    var display = new CS_DisplaySetting();
    foreach (var gunId in shownGuns)
        display.KODBGINIGOL.Add(new CS_DisplaySetting.Types.MKMAFGIOPAN { GunId = gunId });
    main.Send(display);
    var displayReply = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_DisplaySetting>().FirstOrDefault();
    Check("display: two owned characters are shown on the card, in order, with their level and grade",
        displayReply is not null && displayReply.DELPACHDBFG.Count == 2 && displayReply.DELPACHDBFG.Select(d => d.KKNFMCGIFGI.Id).SequenceEqual(shownGuns) && displayReply.DELPACHDBFG.All(d => d.KKNFMCGIFGI.Level >= 1));

    // the real client sends 15 entries, the unused slots as empty entries
    var realShape = new CS_DisplaySetting();
    foreach (var gunId in shownGuns)
        realShape.KODBGINIGOL.Add(new CS_DisplaySetting.Types.MKMAFGIOPAN { GunId = gunId });
    while (realShape.KODBGINIGOL.Count < 15)
        realShape.KODBGINIGOL.Add(new CS_DisplaySetting.Types.MKMAFGIOPAN());
    main.Send(realShape);
    Check("display: the real 15-slot request (empty slots as empty entries) is accepted (a limit of 5 refused it)",
        main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_DisplaySetting>().FirstOrDefault() is { } shape && shape.DELPACHDBFG.Count == shownGuns.Count);
    var stranger = new CS_DisplaySetting();
    stranger.KODBGINIGOL.Add(new CS_DisplaySetting.Types.MKMAFGIOPAN { GunId = 1099 });
    main.Send(stranger);
    Check("display: a character the account does not own is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_DisplaySetting));
}

if (scenario is "all" or "battlepass")
{
    // Battle pass: level from the BP experience item (601), every level claimed once per track, task lists with refresh.
    main.Send(new CS_GetBattlepassInfo());
    var info = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GetBattlepassInfo>().FirstOrDefault();
    Check("battlepass: info lists the current season with 4 daily, 9 weekly and 6 season tasks",
        info?.Battlepass is { CurrId: 30034 } bp && bp.DailyTask.Count == 4 && bp.WeeklyTask.Count == 9 && bp.OPJONAGNMKE.Count == 6,
        $"curr={info?.Battlepass?.CurrId}");

    uint exp = info?.Battlepass?.Exp ?? 0;
    main.Send(new CS_GetBattlepassReward { Type = BattlepassType.None, RewardLevel = 1, RewardType = BpRewardGetType.GetTypeAll });
    var firstClaim = main.ReadFor(TimeSpan.FromSeconds(2));
    var firstReply = firstClaim.Select(p => p.Message).OfType<SC_GetBattlepassReward>().FirstOrDefault();
    bool granted = firstClaim.Any(p => p.Message is SC_ResourceUpdate or SC_Items);
    var firstError = firstClaim.Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
    Check("battlepass: level 1 reward is granted when the experience reaches it; otherwise the claim is refused (BattlepassGeted)",
        exp >= 100 ? firstReply is not null && granted : firstReply is null && !granted && firstError?.ErrorNo == -12602, $"exp={exp} granted={granted} error={firstError?.ErrorNo}");

    main.Send(new CS_GetBattlepassReward { Type = BattlepassType.None, RewardLevel = 1, RewardType = BpRewardGetType.GetTypeAll });
    var secondClaim = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("battlepass: claiming level 1 again grants nothing", !secondClaim.Any(p => p.Message is SC_ResourceUpdate or SC_Items));

    main.Send(new CS_GetBattlepassReward { Type = BattlepassType.None, RewardLevel = 0, RewardType = BpRewardGetType.GetTypeAll });
    var allClaim = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("battlepass: claiming every level grants the remaining reached levels (none when below level 2)",
        allClaim.Any(p => p.Message is SC_ResourceUpdate or SC_Items) == (exp >= 200), $"exp={exp}");

    main.Send(new CS_BattlepassGetExtraReward());
    var extraClaim = main.ReadFor(TimeSpan.FromSeconds(2));
    uint expectedExtra = exp / 100 > 60 ? Math.Min(exp / 100 - 60, 50u) : 0;
    Check("battlepass: extra rewards beyond level 60 are counted once (fresh account)",
        extraClaim.Select(p => p.Message).OfType<SC_BattlepassGetExtraReward>().FirstOrDefault()?.Exp == expectedExtra,
        $"expected={expectedExtra}");

    main.Send(new CS_BattlepassGetDailyTask { TaskType = BpTaskType.Daily });
    var daily = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_BattlepassGetDailyTask>().FirstOrDefault();
    Check("battlepass: daily task list has 4 tasks", daily is not null && daily.Tasks.Count == 4);

    main.Send(new CS_BattlepassRefreshTask { TaskType = BpTaskType.Daily, Idx = 0 });
    var refreshed = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_BattlepassRefreshTask>().FirstOrDefault();
    Check("battlepass: refreshing a daily task replaces it with another and counts the refresh",
        refreshed is not null && refreshed.StcId != 0 && refreshed.RefreshNum >= 1 && daily is not null && !daily.Tasks.Contains(refreshed.StcId));

    main.Send(new CS_BattlepassAcceptTask { TaskId = 1 });
    var shared = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_BattlepassAcceptTask>().FirstOrDefault();
    Check("battlepass: shared tasks answer that the task does not exist", shared?.State == BpShareTaskState.NoExist);
}

if (scenario == "cooking")
{
    // Lobby kitchen and café (opt-in: needs ingredients and hash leaves the account does not have; run seed-items.py first:
    // 1731001=20 1731003=10 1731005=5 1731006=5 1610000=100). Rules are the documented ASSUMPTIONS of LobbyCookingService.
    main.Send(new CS_Guns());
    uint cookGun = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().FirstOrDefault()?.Guns.FirstOrDefault()?.Id ?? 0;

    // Level-ups of the dinner buff (LobbyCookBuffData col 13 = 1610000:100) and of a coffee kind (LobbyCafeBuffData col 12 = 1610000:300)
    main.Send(new CS_LobbyCookBuffUpgrade { Type = 1 });
    var buffUp = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).ToList();
    Check("cooking: raising dinner buff group 1 pays 100 of 1610000 and reports level 2",
        buffUp.OfType<SC_LobbyCookBuffUpgrade>().Any(u => u.Type == 1 && u.JOMLDNKALNP == 2) && buffUp.Any(m => m is SC_Items or SC_ResourceUpdate),
        string.Join(",", buffUp.Select(m => m.GetType().Name)));
    main.Send(new CS_LobbyBuffInfo());
    var buffLevels = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyBuffInfo>().FirstOrDefault();
    Check("cooking: the buff info lists the new dinner level", buffLevels?.AJLCMPKKNAA.GetValueOrDefault(1u) == 2u);
    main.Send(new CS_LobbyCafeCoffeeUpgrade { Type = 1 });
    Check("cooking: a coffee level-up without enough of the material (300 needed) is refused with ItemNotEnough",
        main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo == -10015));

    CS_LobbyDishCook Cook(params uint[] ingredients)
    {
        var c = new CS_LobbyDishCook { GunId = cookGun };
        c.BJIMNEJBOEJ.AddRange(ingredients);
        return c;
    }

    main.Send(Cook(1731001, 1731001));
    var cooked = main.ReadFor(TimeSpan.FromSeconds(2));
    var cookResult = cooked.Select(p => p.Message).OfType<SC_LobbyDishCook>().FirstOrDefault();
    Check("cooking: two fish cubes make dish 1741001 at rank 4 (success, first time)",
        cookResult is not null && cookResult.Result == SC_LobbyDishCook.Types.ICIFJHKELNP.Success && cookResult.CGFMCGJBBKB.ItemId == 1741001 && cookResult.CGFMCGJBBKB.Rank == 4 && cookResult.NFDNIGCBNEF,
        $"result={cookResult?.Result} dish={cookResult?.CGFMCGJBBKB?.ItemId} rank={cookResult?.CGFMCGJBBKB?.Rank}");
    Check("cooking: the ingredients are spent, the dish enters the bag and proficiency rises to 1",
        cooked.Select(p => p.Message).OfType<SC_Items>().Any(i => i.Items.ContainsKey(1731001u))
        && cooked.Select(p => p.Message).OfType<SC_UpdateDishNum>().Any(u => u.ItemId == 1741001 && u.Rank == 4 && u.Num == 1)
        && cooked.Select(p => p.Message).OfType<SC_LobbyCookingProficiency>().Any(p => p.DHNFIEFDMCD == 1));

    main.Send(new CS_DishBagInfo());
    var bag = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_DishBagInfo>().FirstOrDefault();
    Check("cooking: SC_DishBagInfo lists dish 1741001 / rank 4 / buff 0 x1",
        bag is not null && bag.Info.DABKAEIBBGA.TryGetValue(1741001, out var bagRanks) && bagRanks.HNGELNIBCMN.TryGetValue(4, out var bagBuffs) && bagBuffs.PJNFOIALIFH.TryGetValue(0, out uint bagCount) && bagCount == 1);

    main.Send(new CS_LobbyCookingInfo());
    var cookInfo = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyCookingInfo>().FirstOrDefault();
    Check("cooking: SC_LobbyCookingInfo has proficiency 1 and the dish unlocked, not claimed",
        cookInfo is not null && cookInfo.Info.CLLCFMCMONP == 1 && cookInfo.Info.DJCLEPBDFJD.TryGetValue(1, out var cookGroup)
        && cookGroup.GMEAMJPGLBH.TryGetValue(1741001, out var cookFlags) && !cookFlags.NHOAAJOECCK && !cookFlags.BGJNHNFFEKK);

    main.Send(Cook(1731001));
    var failed = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("cooking: a combination without recipe fails and the ingredient is lost",
        failed.Select(p => p.Message).OfType<SC_LobbyDishCook>().Any(r => r.Result == SC_LobbyDishCook.Types.ICIFJHKELNP.Fail)
        && failed.Select(p => p.Message).OfType<SC_Items>().Any() && !failed.Any(p => p.Message is SC_UpdateDishNum));

    main.Send(Cook(1731009, 1731009));
    Check("cooking: ingredients the account does not own are refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LobbyDishCook));

    // illustrated book
    main.Send(new CS_LobbyDishIndexClaim { BJIMNEJBOEJ = { 1741002 } });
    Check("cooking: claiming a dish that was never cooked is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LobbyDishIndexClaim));
    main.Send(new CS_LobbyDishIndexClaim { BJIMNEJBOEJ = { 1741001 } });
    var claim = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("cooking: claiming dish 1741001 grants its 5 hash leaves",
        claim.Select(p => p.Message).OfType<SC_LobbyDishIndexClaim>().Any(c => c.BJIMNEJBOEJ.Contains(1741001u))
        && claim.Select(p => p.Message).OfType<SC_Items>().Any(i => i.Items.TryGetValue(1610000u, out int leaves) && leaves >= 5));
    main.Send(new CS_LobbyDishIndexClaim { BJIMNEJBOEJ = { 1741001 } });
    Check("cooking: claiming it twice is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LobbyDishIndexClaim));

    main.Send(new CS_LobbyDishIndexMilestoneClaim { GroupId = 1 });
    var milestone = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("cooking: milestone 1 (1 dish) is claimable once", milestone.Select(p => p.Message).OfType<SC_LobbyDishIndexMilestoneClaim>().Any(m => m.GroupId == 1 && m.Level == 1));
    main.Send(new CS_LobbyDishIndexMilestoneClaim { GroupId = 1 });
    Check("cooking: milestone 2 (3 dishes) is refused with one dish", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LobbyDishIndexMilestoneClaim));

    main.Send(new CS_LobbyDishIndexFavorite { ItemId = 1741001 });
    Check("cooking: favourite toggles on", main.ReadFor(TimeSpan.FromSeconds(1)).Select(p => p.Message).OfType<SC_LobbyDishIndexFavorite>().Any(f => f.ItemId == 1741001 && f.BGJNHNFFEKK));

    // gifts: 1741020 (affection dish, 50 points at rank 4), 1741006 / 1741012 (property dishes)
    main.Send(Cook(1731001, 1731005));
    main.Send(Cook(1731003, 1731003));
    main.Send(Cook(1731003, 1731003));
    main.Send(Cook(1731006, 1731006));
    var more = main.ReadFor(TimeSpan.FromSeconds(3));
    Check("cooking: four more dishes cooked (proficiency 5: the failed attempt gives none)",
        more.Select(p => p.Message).OfType<SC_LobbyCookingProficiency>().Any(p => p.DHNFIEFDMCD == 5), $"results={more.Select(p => p.Message).OfType<SC_LobbyDishCook>().Count()}");

    CS_LobbyDishGift Gift(IKMBDMBBGMH type, params (uint dish, uint num)[] items)
    {
        var g = new CS_LobbyDishGift { Type = type, GunId = cookGun };
        foreach (var (dish, num) in items)
            g.LIMCAHKNPFJ.Add(new CS_LobbyDishGift.Types.MOMMFHIJPBK { ItemId = dish, Rank = 4, Num = num });
        return g;
    }

    main.Send(Gift(IKMBDMBBGMH.Love, (1741020, 1)));
    var loveGift = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("cooking: an affection dish gives 50 affection experience and leaves the bag",
        loveGift.Select(p => p.Message).OfType<SC_LoveLevelUpdate>().Any(l => l.GunId == cookGun && l.JLHFICPMAEI >= 1 && l.FDKIHAPHHPD == 0)
        && loveGift.Select(p => p.Message).OfType<SC_UpdateDishNum>().Any(u => u.ItemId == 1741020 && u.Num == 0));
    main.Send(Gift(IKMBDMBBGMH.Love, (1741020, 1)));
    Check("cooking: gifting a dish that is no longer in the bag is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LoveLevelUpdate));

    main.Send(Gift(IKMBDMBBGMH.Property, (1741006, 1), (1741012, 1)));
    Check("cooking: property dishes of different properties cannot be gifted together", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_GunDishPropertyExpUp));
    main.Send(Gift(IKMBDMBBGMH.Property, (1741006, 1)));
    var prop1 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunDishPropertyExpUp>().FirstOrDefault();
    Check("cooking: one property dish gives 4 satiety (below level 1)", prop1 is not null && prop1.GunId == cookGun && prop1.EEPPFPLKGKN == 4 && prop1.EMOLDIANHCJ == 0, $"total={prop1?.EEPPFPLKGKN} level={prop1?.EMOLDIANHCJ}");
    main.Send(Gift(IKMBDMBBGMH.Property, (1741006, 1)));
    var prop2 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunDishPropertyExpUp>().FirstOrDefault();
    Check("cooking: the second one reaches 8 satiety, still level 0 (level 1 needs 10)", prop2 is not null && prop2.EEPPFPLKGKN == 8 && prop2.EMOLDIANHCJ == 0, $"total={prop2?.EEPPFPLKGKN} level={prop2?.EMOLDIANHCJ}");

    // daily dinner and coffee
    main.Send(new CS_LobbyBuffInfo());
    var buff0 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyBuffInfo>().FirstOrDefault();
    Check("cooking: before the dinner the buff info is empty", buff0 is not null && buff0.KFNCPJPPGEF == 0 && !buff0.LHGLNJNFIPM);
    main.Send(new CS_LobbyStartCook { Type = 1, GunId = cookGun });
    var start1 = main.ReadFor(TimeSpan.FromSeconds(2));
    var startMsg = start1.Select(p => p.Message).OfType<SC_LobbyStartCook>().FirstOrDefault();
    Check("cooking: the first dinner of the day is free and activates group 1", startMsg is not null && startMsg.KFNCPJPPGEF == 1 && startMsg.Result == 1 && startMsg.LHGLNJNFIPM && !start1.Any(p => p.Message is SC_Items));
    main.Send(new CS_LobbyBuffInfo());
    var buff1 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyBuffInfo>().FirstOrDefault();
    Check("cooking: the buff info keeps the dinner (survives a new request)", buff1 is not null && buff1.KFNCPJPPGEF == 1 && buff1.LHGLNJNFIPM);
    main.Send(new CS_LobbyStartCook { Type = 2, GunId = cookGun });
    var start2 = main.ReadFor(TimeSpan.FromSeconds(2));
    Check("cooking: a second dinner costs 20 hash leaves", start2.Any(p => p.Message is SC_LobbyStartCook) && start2.Select(p => p.Message).OfType<SC_Items>().Any(i => i.Items.ContainsKey(1610000u)));
    main.Send(new CS_LobbyStartCook { Type = 99, GunId = cookGun });
    Check("cooking: an unknown dinner group is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LobbyStartCook));

    main.Send(new CS_LobbyCafeMakeCoffee { Type = 1 });
    var coffee = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyCafeMakeCoffee>().FirstOrDefault();
    Check("cooking: coffee kind 1 gives buff row 1", coffee is not null && coffee.Type == 1 && coffee.FMICMDLIOJD == 1);
    main.Send(new CS_LobbyCafeMakeCoffee { Type = 2 });
    Check("cooking: only one coffee per day", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_LobbyCafeMakeCoffee));
    main.Send(new CS_LobbyGetCafeInfo());
    Check("cooking: the café info carries today's coffee", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyGetCafeInfo>().Any(c => c.FMICMDLIOJD == 1));
}

if (scenario == "equip")
{
    // Costumes, costume parts and weapon mods (accessories) must survive a new login: they used to be echoed and lost.
    main.Send(new CS_Guns());
    var equipGuns = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().SelectMany(g => g.Guns).ToList();
    uint equipGun = equipGuns.Select(g => g.Id).FirstOrDefault(id => id == 1017);
    Check("equip: the starter character 1017 exists", equipGun == 1017);

    main.Send(new CS_GunCostume { GunId = 1017, CostumeId = 1101703 });
    Check("equip: a ClothesData costume missing from CostumeData (1101703 of 1017) is accepted: it was refused with error 10114 in the real client",
        main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunCostume>().Any(c => c.GunId == 1017 && c.CostumeId == 1101703));
    main.Send(new CS_GunCostume { GunId = 1017, CostumeId = 1101701 });
    Check("equip: costume 1101701 is accepted for character 1017",
        main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunCostume>().Any(c => c.GunId == 1017 && c.CostumeId == 1101701));
    main.Send(new CS_GunCostume { GunId = 1017, CostumeId = 1100800 });
    Check("equip: the costume of another character is refused", !main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_GunCostume));
    main.Send(new CS_GunCostumeParts { GunId = 1017, CostumeId = 1101701, CostumeParts = 273, LLLFHFLGNCH = BAGPCFGACEF.Types.GCBADMLINMA.Barrack });
    Check("equip: costume parts are answered", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunCostumeParts>().Any(c => c.CostumeParts == 273));

    main.Send(new CS_Chat { Uid = 1, Message = "/inventory addall weaponmod" });
    main.ReadFor(TimeSpan.FromSeconds(8));
    main.Send(new CS_GunWeaponMods());
    var equipMods = main.ReadFor(TimeSpan.FromSeconds(8)).Select(p => p.Message).OfType<SC_GunWeaponMods>().SelectMany(m => m.Mods).Take(2).ToList();
    main.Send(new CS_GunWeapons());
    var equipWeapon = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeapons>().SelectMany(w => w.Weapons).FirstOrDefault(w => w.GunId == 1017);
    Check("equip: the account has a weapon of 1017 and two mods", equipWeapon is not null && equipMods.Count == 2, $"weapon={equipWeapon?.Id} mods={equipMods.Count}");
    if (equipWeapon is not null && equipMods.Count == 2)
    {
        main.Send(new CS_GunWeaponModBelong { WeaponId = equipWeapon.Id, Slot = 1, WeaponModId = equipMods[0].Id });
        Check("equip: a mod goes into slot 1", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunWeaponModBelong>().Any(b => b.WeaponId == equipWeapon.Id && b.Slot == 1 && b.WeaponModId == equipMods[0].Id));
        main.Send(new CS_GunWeaponModBelong { WeaponId = equipWeapon.Id, Slot = 2, WeaponModId = equipMods[1].Id });
        main.ReadFor(TimeSpan.FromSeconds(2));
        main.Send(new CS_GunWeaponModBelong { WeaponId = equipWeapon.Id, Slot = 2, WeaponModId = equipMods[0].Id });
        var moved = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunWeaponModBelong>().ToList();
        Check("equip: moving the first mod to slot 2 frees slot 1 and takes the second mod out",
            moved.Any(b => b.WeaponId == 0 && b.Slot == 1 && b.WeaponModId == equipMods[0].Id) && moved.Any(b => b.WeaponId == 0 && b.Slot == 2 && b.WeaponModId == equipMods[1].Id)
            && moved.Any(b => b.WeaponId == equipWeapon.Id && b.Slot == 2 && b.WeaponModId == equipMods[0].Id), $"answers={moved.Count}");
    }

    var equipAgain = await Connect(true);
    equipAgain.ReadFor(TimeSpan.FromSeconds(20));
    equipAgain.Send(new CS_Guns());
    var gunAfter = equipAgain.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().SelectMany(g => g.Guns).FirstOrDefault(g => g.Id == 1017);
    Check("equip: after a new login character 1017 still wears costume 1101701 with parts 273", gunAfter is not null && gunAfter.Costume == 1101701 && gunAfter.CostumeParts == 273,
        $"costume={gunAfter?.Costume} parts={gunAfter?.CostumeParts}");
    equipAgain.Send(new CS_GunWeapons());
    var weaponAfter = equipAgain.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeapons>().SelectMany(w => w.Weapons).FirstOrDefault(w => w.GunId == 1017);
    Check("equip: after a new login the weapon keeps exactly the mod put in slot 2", weaponAfter is not null && equipMods.Count == 2 && weaponAfter.WeaponMods.Count == 1 && weaponAfter.WeaponMods[0].Id == equipMods[0].Id,
        $"mods={weaponAfter?.WeaponMods.Count}");
    equipAgain.Dispose();
}

if (scenario is "all" or "restored")
{
    // Requests the 22:00 server log showed answered with "function locked" (-10018) or with a constant: now stored and applied.
    main.Send(new CS_SetBgm { JNHHAINDALC = 7 });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_GetBgm());
    var bgmReply = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GetBgm>().FirstOrDefault();
    Check("restored: the music chosen with SetBgm is what GetBgm returns", bgmReply?.JNHHAINDALC == 7, $"bgm={bgmReply?.JNHHAINDALC}");

    main.Send(new CS_SetLobbyMeilingConfig { ECGAHCPCDDI = { 11u, 12u } });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_GetLobbyMeilingConfig());
    var meiling = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GetLobbyMeilingConfig>().FirstOrDefault();
    Check("restored: the selection of SetLobbyMeilingConfig is what GetLobbyMeilingConfig returns", meiling is not null && meiling.ECGAHCPCDDI.SequenceEqual([11u, 12u]));

    main.Send(new CS_SetNameCardGameplaySlots { GPDFKEMELKI = { 3u, 4u } });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_QueryNameCardGameplayOptions());
    var cardSlots = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_QueryNameCardGameplayOptions>().FirstOrDefault();
    Check("restored: the name card gameplay slots are returned by the options query", cardSlots is not null && cardSlots.GPDFKEMELKI.SequenceEqual([3u, 4u]));

    main.Send(new CS_ActivityBackCheckIn { EventId = 1 });
    var backIn = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).ToList();
    Check("restored: the welcome-back check-in grants its day-1 reward and reports the day",
        backIn.OfType<SC_ActivityBackCheckIn>().Any(c => c.IOLHEKBEICG == 1) && backIn.Any(m => m is SC_ResourceUpdate or SC_Items), string.Join(",", backIn.Select(m => m.GetType().Name)));
    main.Send(new CS_ActivityBackCheckIn { EventId = 1 });
    Check("restored: the check-in cannot be claimed twice the same day", main.ReadFor(TimeSpan.FromSeconds(1)).Select(p => p.Message).OfType<SC_ReportError>().Any());
    main.Send(new CS_ActivityBackInfo());
    var backInfo = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ActivityBackInfo>().FirstOrDefault();
    Check("restored: ActivityBackInfo reports the check-in as done", backInfo?.Info?.CheckinDone == true && backInfo.Info.CJOINHJCPII == 1);

    main.Send(new CS_Guns());
    uint talentGun = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().FirstOrDefault()?.Guns.FirstOrDefault()?.Id ?? 0;
    main.Send(new CS_GunPublicTalentSkillItemBelong2 { GunId = talentGun, PublicTalentSkillItemUid = 1843288605358841857UL });
    var belong = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunPublicTalentSkillItemBelong2>().FirstOrDefault();
    Check("restored: a public talent skill item can be put on a character (no slot field = first slot)",
        belong is { Gun1.GunId: > 0 } && belong.Gun1.PublicSkillItemUid[0] == 1843288605358841857UL && belong.Public1.StcId == 91025 && belong.Public1.GunId == talentGun,
        $"gun={belong?.Gun1?.GunId} public1={belong?.Public1?.StcId}");
    main.Send(new CS_PublicTalentSkillItems());
    var talentItems = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_PublicTalentSkillItems>().FirstOrDefault();
    Check("restored: the item list reports which character wears the item", talentItems?.PublicSkillItems.FirstOrDefault(i => i.StcId == 91025)?.GunId == talentGun);

    main.Send(new CS_LobbyPartyJoin { HCKMOMCKFEA = 5, DOGDHOGHFNH = new FCGCLLGILIJ { Id = 5, SectionId = 2 } });
    var joined = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyPartyJoin>().FirstOrDefault();
    main.Send(new CS_LobbyPartyInfo());
    var partyNow = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_LobbyPartyInfo>().FirstOrDefault();
    Check("restored: joining a party is answered and reflected by LobbyPartyInfo", joined?.HCKMOMCKFEA == 5 && partyNow?.LKIBNJBDDJI == 5 && partyNow.DOGDHOGHFNH?.SectionId == 2);
    main.Send(new CS_LobbyPartyLeave());
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_LobbyPartyInfo());
    var partyLeft = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_LobbyPartyInfo>().FirstOrDefault();
    Check("restored: leaving the party clears it", partyLeft is not null && partyLeft.LKIBNJBDDJI == 0);

    // Daily quests whose action the server cannot observe (PvP 6, puzzle exchange 8, Darkov front 14) count as done; the ones it counts (5 supply battle,
    // 9 gifts) are NOT claimable before the action happens.
    main.Send(new CS_CommonQuestTakeReward { Type = CommonQuestCounters.Types.Type.Daily, QuestId = { 5u, 6u, 8u, 9u, 14u } });
    var questClaim = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).ToList();
    Check("restored: claiming pays the quests the server cannot observe (6, 8, 14) and not the ones that need a real action (5, 9)",
        questClaim.OfType<SC_CommonQuestTakeReward>().FirstOrDefault() is { } qr && qr.QuestId.OrderBy(i => i).SequenceEqual([6u, 8u, 14u])
        && questClaim.Any(m => m is SC_ResourceUpdate or SC_Items), string.Join(",", questClaim.Select(m => m.GetType().Name)));
    main.Send(new CS_CommonQuestTakeReward { Type = CommonQuestCounters.Types.Type.Daily, QuestId = { 6u } });
    Check("restored: a claimed daily quest is not paid twice", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_CommonQuestTakeReward>().All(r => r.QuestId.Count == 0));

    // Sweep of a stage that is already passed (10103 is one of the opening stages the account starts with): pays its repeatable drops and charges energy
    main.Send(new CS_Raid { StcStageId = 10103, RaidTimes = 2 });
    var raid = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).ToList();
    Check("restored: a sweep of a passed stage is answered with its drops (SC_Raid) and is not refused",
        raid.OfType<SC_Raid>().Any(r => r.StcStageId == 10103 && r.RaidTimes == 2) && !raid.OfType<SC_ReportError>().Any(), string.Join(",", raid.Select(m => m.GetType().Name)));
    main.Send(new CS_Raid { StcStageId = 10103, RaidTimes = 15 });
    var raidMany = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).ToList();
    Check("restored: a sweep of more than 10 runs (the real client asked for 24) is answered, limited only by energy",
        raidMany.OfType<SC_Raid>().Any(r => r.RaidTimes == 15) || raidMany.OfType<SC_ReportError>().Any(e => e.ErrorNo != -10008), string.Join(",", raidMany.Select(m => m.GetType().Name)));
    main.Send(new CS_Raid { StcStageId = 26301, RaidTimes = 1 });
    Check("restored: a stage that was never passed cannot be swept", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any());
    main.Send(new CS_MultiQuestTypeTakeReward { Types_ = { CommonQuestCounters.Types.Type.Daily } });
    Check("restored: claim-all of the quest tabs is answered", main.ReadFor(TimeSpan.FromSeconds(2)).Any(p => p.Message is SC_MultiQuestTypeTakeReward));

    main.Send(new CS_LobbyAsmrUnlockScene { IAAPPCGFLMD = 2 });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_LobbyAsmrInfo());
    var asmr = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyAsmrInfo>().FirstOrDefault();
    Check("restored: an unlocked ASMR scene stays unlocked in LobbyAsmrInfo", asmr is not null && asmr.IIIOINLCDBH.Contains(2u) && asmr.IIIOINLCDBH.Contains(1u));

    main.Send(new CS_GunTalentReset { TalentId = talentGun });
    Check("restored: a talent tree reset is answered (and refuses nothing for a character that owns the tree)",
        main.ReadFor(TimeSpan.FromSeconds(2)).Any(p => p.Message is SC_GunTalentReset { TalentId: > 0 }));

    // Dormitory: the love character chosen with DormSetLove comes back in DormInfo
    main.Send(new CS_DormSetLove { GunId = talentGun });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_DormInfo());
    var dormInfo = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_DormInfo>().FirstOrDefault();
    Check("restored: DormInfo reports the love character chosen with DormSetLove (the dormitory costumes stay those of the capture: the battle costume put the models in T-pose)",
        dormInfo?.Info is { } di && di.KBCJNGDOGKJ == talentGun, $"love={dormInfo?.Info?.KBCJNGDOGKJ} expected={talentGun}");

    // U-01: the dormitory costume and its parts are kept and returned by DormInfo (not the battle costume, which put the models in T-pose)
    main.Send(new CS_DormSkinChange { GunId = 1017, CostumeId = 1108001 });
    Check("restored: a dormitory costume of another character is refused", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any());
    main.Send(new CS_DormSkinChange { GunId = 1017, CostumeId = 1101703 });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_LobbyGunCostumeParts { GunId = 1017, CostumeId = 1101703, CostumeParts = 273 });
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_DormInfo());
    var dormSkin = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_DormInfo>().FirstOrDefault();
    Check("restored: DormInfo returns the costume and parts chosen in the dormitory wardrobe",
        dormSkin?.Info?.PIOFFPPPEPK.GetValueOrDefault(1017u)?.PPMIBJEJABD == 1101703u && dormSkin.GDJOKNOBNBM.GetValueOrDefault(1101703u) == 273u,
        $"costume={dormSkin?.Info?.PIOFFPPPEPK.GetValueOrDefault(1017u)?.PPMIBJEJABD} parts={dormSkin?.GDJOKNOBNBM.GetValueOrDefault(1101703u)}");

    // Lobby part: refused without its price (item 202 x50), unlocked and reported by the build info once paid
    main.Send(new CS_LobbyUnlockPart { EGINIIGDCBO = 10410 });
    Check("restored: unlocking a lobby part without its price is refused (ItemNotEnough)",
        main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo == -10015));
    main.Send(new CS_Chat { Uid = 1, Message = "/inventory provision" });
    main.ReadFor(TimeSpan.FromSeconds(8));
    main.Send(new CS_LobbyUnlockPart { EGINIIGDCBO = 10410 });
    var partMsgs = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).ToList();
    main.Send(new CS_LobbyGetBuildInfo());
    var buildInfo = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_LobbyGetBuildInfo>().FirstOrDefault();
    Check("restored: once paid, a lobby part is unlocked and the build info lists it in its room",
        partMsgs.OfType<SC_LobbyUnlockPart>().Any(u => u.EGINIIGDCBO == 10410) && buildInfo?.CPJOGIOCNMM.GetValueOrDefault(104u)?.KICFGBGNDGF.Contains(10410u) == true,
        string.Join(",", partMsgs.Select(m => m.GetType().Name)));

    main.Send(new CS_SimCombatMythicHardHistory());
    Check("restored: the mythic hard history is answered", main.ReadFor(TimeSpan.FromSeconds(2)).Any(p => p.Message is SC_SimCombatMythicHardHistory));

    main.Send(new CS_ControlRoomWithdrawStamina { Num = 5 });
    Check("restored: withdrawing stamina the room has not produced is refused (ItemNotEnough), not a locked-function error",
        main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo == -10015));
}

if (scenario is "all" or "dorm")
{
    main.Send(new CS_DormInfo());
    var d = main.ReadFor(TimeSpan.FromSeconds(3));
    main.Dump("dorm", d);
    Check("dorm: SC_DormInfo returned", d.Any(p => p.Message is SC_DormInfo));
}

// Each focused scenario is an independent smoke. Do not run gacha/inventory/reconnect side effects
// after a caller explicitly selected the login-only scenario.
if (scenario == "probe")
{
    // Audit tool (opt-in, not part of "all"): sends each request named in the file PROBE_FILE (one MsgCs name per line, without the MsgCs
    // prefix) with an EMPTY body and prints what the server does: the reply messages, or UNANSWERED / BLOCKED (SC_ReportError -10018 =
    // "function locked", which the dispatcher sends for requests without an implementation).
    foreach (var line in File.ReadAllLines(Environment.GetEnvironmentVariable("PROBE_FILE") ?? "probe.txt"))
    {
        var name = line.Trim();
        if (name.Length == 0) continue;
        var type = typeof(CS_Login).Assembly.GetType("Griffin.Common.Proto.CS_" + name);
        if (type is null || Activator.CreateInstance(type) is not Google.Protobuf.IMessage msg) { Console.WriteLine($"PROBE {name}: no message class"); continue; }
        main.Send(msg);
        var got = main.ReadFor(TimeSpan.FromMilliseconds(350));
        string result = got.Count == 0 ? "UNANSWERED"
            : string.Join(",", got.Select(p => p.Message is SC_ReportError e ? (e.ErrorNo == -10018 ? "BLOCKED(-10018)" : $"error{e.ErrorNo}") : p.MsgId.ToString().Replace("MsgSc", "")));
        Console.WriteLine($"PROBE {name}: {result}");
    }
}

if (scenario == "campaign")
{
    // Sweep of EVERY stage of the tables (CAMPAIGN_FILE written by a helper script; run the server with GRIFFIN_FREE_STAGES=1):
    // StageStart -> StageInto -> StageResult(Win) and the record it must leave. Prints the failures grouped by stage type. Opt-in.
    var failuresByType = new SortedDictionary<int, List<string>>();
    var okByType = new SortedDictionary<int, int>();
    var gateIds = new List<uint>();
    void Fail(int type, string text) { if (!failuresByType.TryGetValue(type, out var l)) failuresByType[type] = l = []; l.Add(text); }
    var campaignRange = (Environment.GetEnvironmentVariable("CAMPAIGN_RANGE") ?? "0-999999").Split('-').Select(int.Parse).ToArray();
    int stageIndex = -1;
    main.ReadFor(TimeSpan.FromSeconds(2)); // late login pushes would be read as the replies of the first stage
    foreach (var line in File.ReadAllLines(Environment.GetEnvironmentVariable("CAMPAIGN_FILE") ?? "campaign.txt"))
    {
        var f = line.Split(' ');
        if (f[0] == "G") { gateIds.Add(uint.Parse(f[1])); continue; }
        if (f[0] != "S") continue;
        if (++stageIndex < campaignRange[0] || stageIndex > campaignRange[1]) continue;
        uint stageId = uint.Parse(f[1]); int stageType = int.Parse(f[2]);
        // the three requests go out back to back (the server answers each connection in order) and the replies are read once
        main.Send(new CS_StageStart { StcStageId = stageId });
        main.Send(new CS_StageInto());
        main.Send(new CS_StageResult { BattleResult = BattleResult.Win, Count = 1 });
        var all3 = main.ReadFor(TimeSpan.FromMilliseconds(150)).Select(p => p.Message).ToList();
        if (!(all3.OfType<SC_StageStart>().Any() && all3.OfType<SC_StageInto>().Any() && all3.OfType<SC_StageResult>().Any()) && !all3.OfType<SC_ReportError>().Any())
            all3.AddRange(main.ReadFor(TimeSpan.FromMilliseconds(500)).Select(p => p.Message));
        if (all3.OfType<SC_ReportError>().FirstOrDefault() is { } startError) { Fail(stageType, $"{stageId}: error {startError.ErrorNo}"); continue; }
        if (all3.OfType<SC_StageStart>().FirstOrDefault() is not { } sc || sc.Stage?.StcStageId != stageId) { Fail(stageType, $"{stageId}: no SC_StageStart (got {string.Join(",", all3.Select(m => m.GetType().Name))})"); continue; }
        if (all3.OfType<SC_StageInto>().FirstOrDefault()?.Stage?.StcStageId != stageId) { Fail(stageType, $"{stageId}: no SC_StageInto"); continue; }
        var res = all3.OfType<SC_StageResult>().FirstOrDefault();
        if (res is null || res.StageId != stageId || res.BattleResult != BattleResult.Win) { Fail(stageType, $"{stageId}: result {res?.StageId}/{res?.BattleResult}"); continue; }
        if (!all3.OfType<SC_StageRecordUpdate>().Any(r => r.Record?.StageId == stageId && r.Record.FirstPassTime != 0)) { Fail(stageType, $"{stageId}: no record update"); continue; }
        okByType[stageType] = okByType.GetValueOrDefault(stageType) + 1;
        if (stageIndex % 250 == 0) Console.WriteLine($"CAMPAIGN progress {stageIndex}");
    }

    main.Send(new CS_QuestCount());
    var sweepAch = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_QuestCount>().FirstOrDefault()?.Quest.Counters.FirstOrDefault(c => c.Type == CommonQuestCounters.Types.Type.Achievement);
    var missingGates = gateIds.Where(g => sweepAch is null || !sweepAch.Rewards.ContainsKey(g)).ToList();
    foreach (var (type, ok) in okByType) Console.WriteLine($"CAMPAIGN type {type}: {ok} ok, {failuresByType.GetValueOrDefault(type)?.Count ?? 0} failed");
    foreach (var (type, list) in failuresByType)
        foreach (var text in list.Take(15)) Console.WriteLine($"CAMPAIGN FAIL type {type} {text}");
    Check("campaign: every stage of the tables starts, is entered and resolves a Win with a record", failuresByType.Count == 0, $"failed={failuresByType.Values.Sum(l => l.Count)}");
    Check("campaign: after clearing every stage every chapter gate achievement that depends on stages/level is complete", missingGates.Count == 0, $"missing={string.Join(",", missingGates)}");
}

if (scenario == "replay")
{
    // Replays the requests (with their real bodies) that the real client sent, taken from the server logs: lines "MsgCsName: { json }" in REPLAY_FILE.
    // Prints one line per request: the replies, or the error number; BLOCKED = the "function locked" error, SILENT = no reply. Opt-in (changes the account).
    var jsonParser = new Google.Protobuf.JsonParser(Google.Protobuf.JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
    foreach (var line in File.ReadAllLines(Environment.GetEnvironmentVariable("REPLAY_FILE") ?? "replay.txt"))
    {
        int colon = line.IndexOf(": ", StringComparison.Ordinal);
        if (!line.StartsWith("MsgCs") || colon < 0) continue;
        string name = line[5..colon];
        var type = typeof(CS_Login).Assembly.GetType("Griffin.Common.Proto.CS_" + name);
        if (type is null || Activator.CreateInstance(type) is not Google.Protobuf.IMessage msg) { Console.WriteLine($"REPLAY {name}: no message class"); continue; }
        try { msg = jsonParser.Parse(line[(colon + 2)..], msg.Descriptor); }
        catch (Exception ex) { Console.WriteLine($"REPLAY {name}: body not parsed ({ex.Message[..Math.Min(60, ex.Message.Length)]})"); continue; }
        main.Send(msg);
        var got = main.ReadFor(TimeSpan.FromMilliseconds(450));
        string result = got.Count == 0 ? "SILENT"
            : string.Join(",", got.Select(p => p.Message is SC_ReportError e ? (e.ErrorNo == -10018 ? "BLOCKED(-10018)" : $"error{e.ErrorNo}") : p.MsgId.ToString().Replace("MsgSc", "")));
        Console.WriteLine($"REPLAY {name} {line[(colon + 2)..Math.Min(line.Length, colon + 80)]}: {result}");
    }
}

if (scenario is "provision" or "provisionlogin")
{
    // Objects the client tables list but a fresh account lacks (reported as "0/50" on the dormitory currency, interaction keys, gifts...).
    // "provision" runs the explicit command (server with GRIFFIN_AUTO_PROVISION=0); "provisionlogin" expects them right after login (AUTO_PROV=1).
    if (scenario == "provision")
    {
        main.Send(new CS_Chat { Uid = 1, Message = "/inventory provision" });
        main.ReadFor(TimeSpan.FromSeconds(8));
    }

    main.Send(new CS_Items());
    var provItems = main.ReadFor(TimeSpan.FromSeconds(8)).Select(p => p.Message).OfType<SC_Items>().SelectMany(i => i.Items).ToDictionary(kv => kv.Key, kv => kv.Value, comparer: null) ;
    Check("provision: the dormitory currency 1610000, the interaction key 1630001 and the generic gift 300003 are in the inventory with a balance",
        provItems.GetValueOrDefault(1610000u) > 0 && provItems.GetValueOrDefault(1630001u) > 0 && provItems.GetValueOrDefault(300003u) > 0,
        $"1610000={provItems.GetValueOrDefault(1610000u)} 1630001={provItems.GetValueOrDefault(1630001u)} 300003={provItems.GetValueOrDefault(300003u)}");
    Check("provision: kitchen ingredients 1731001-1731011 are provided", Enumerable.Range(1731001, 11).All(id => provItems.GetValueOrDefault((uint)id) > 0));
    Check("provision: battle pass experience (601) is NOT provisioned", provItems.GetValueOrDefault(601u) == 0);
    Check("provision: the ES shop currency (type 304: 3041001 pays the black-market offers) is provided with a balance",
        new uint[] { 3041001, 3041002, 3041003, 3041004, 3041006 }.All(id => provItems.GetValueOrDefault(id) > 0),
        string.Join(",", new uint[] { 3041001, 3041006 }.Select(id => id + "=" + provItems.GetValueOrDefault(id))));

    if (scenario == "provision")
    {
        main.Send(new CS_Chat { Uid = 1, Message = "/inventory provision" });
        main.ReadFor(TimeSpan.FromSeconds(8));
        main.Send(new CS_Items());
        var provAgain = main.ReadFor(TimeSpan.FromSeconds(8)).Select(p => p.Message).OfType<SC_Items>().SelectMany(i => i.Items).ToDictionary(kv => kv.Key, kv => kv.Value, comparer: null);
        Check("provision: running it again is idempotent (balances unchanged, nothing doubled)",
            provAgain.GetValueOrDefault(1610000u) == provItems.GetValueOrDefault(1610000u) && provAgain.GetValueOrDefault(300003u) == provItems.GetValueOrDefault(300003u) && provAgain.Count == provItems.Count);
    }
}

if (scenario == "login")
{
    main.Dispose();
    Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
    return failures == 0 ? 0 : 1;
}

{
    var (gunsFinal, weaponsFinal) = CountOwned(main);
    Console.WriteLine($"       final inventory: guns {gunsFinal}, weapons {weaponsFinal}");
}
main.Dispose();

// ---------- reconnect ----------
var again = await Connect(true);
var relogin = again.ReadFor(TimeSpan.FromSeconds(20));
again.Dump("reconnect", relogin);
Check("reconnect: SC_Login received", relogin.Any(p => p.Message is SC_Login));
var reloginUser = relogin.Select(p => p.Message).OfType<SC_Login>().FirstOrDefault()?.User;
if (changedName is not null)
    Check("profile: name, motto and title survive the next login", reloginUser is not null && reloginUser.Name == changedName && reloginUser.Motto == "protocol smoke" && reloginUser.Title == changedTitle,
        $"name={reloginUser?.Name} motto={reloginUser?.Motto} title={reloginUser?.Title}");
if (scenario == "all")
    Check("reconnect: the pulls made on the skin banner survive the next login (Record.Limit)",
        relogin.Select(p => p.Message).OfType<SC_Record>().FirstOrDefault() is { } reloginRecord && reloginRecord.Limit.TryGetValue(221001u, out uint pulls221) && pulls221 >= 10,
        $"limit={string.Join(",", relogin.Select(p => p.Message).OfType<SC_Record>().FirstOrDefault()?.Limit.Select(kv => kv.Key + ":" + kv.Value) ?? [])}");
var (gunsRe, weaponsRe) = CountOwned(again);
Console.WriteLine($"       inventory after reconnect: guns {gunsRe}, weapons {weaponsRe}");
again.Dispose();

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

static (int guns, int weapons) CountOwned(GameClient c)
{
    c.Send(new CS_Guns());
    int guns = c.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().Sum(m => m.Guns.Count);
    c.Send(new CS_GunWeapons());
    int weapons = c.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeapons>().Sum(m => m.Weapons.Count);
    return (guns, weapons);
}

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>();
    for (int i = 0; i + 1 < a.Length; i += 2)
        d[a[i].TrimStart('-')] = a[i + 1];
    return d;
}

sealed class GameClient : IDisposable
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private ushort seq;

    public GameClient(string host, int port)
    {
        client = new TcpClient();
        client.Connect(host, port);
        stream = client.GetStream();
    }

    public void Send(IMessage message)
    {
        seq++;
        var main = new MainMessage(seq, 0, BasePacket.FromIMessage(message));
        byte[] raw = main.ToRawMessageData();
        stream.Write(raw, 0, raw.Length);
    }

    public void SendRaw(ushort rawMsgId, byte[] payload)
    {
        seq++;
        var main = new MainMessage(seq, 0, BasePacket.FromIMessageBytes(rawMsgId, payload));
        byte[] raw = main.ToRawMessageData();
        stream.Write(raw, 0, raw.Length);
    }

    /// <summary>Reads framed messages until nothing arrives for <paramref name="quiet"/>.</summary>
    public List<BasePacket> ReadFor(TimeSpan quiet)
    {
        var packets = new List<BasePacket>();
        stream.ReadTimeout = (int)quiet.TotalMilliseconds;
        while (true)
        {
            try
            {
                var msg = PacketFramer.Read(stream);
                if (msg is null) break;
                packets.AddRange(msg.Packets);
            }
            catch (IOException) { break; } // read timeout
        }
        return packets;
    }

    public void Dump(string label, List<BasePacket> packets)
    {
        Console.WriteLine($"  -- {label}: {packets.Count} packet(s): " +
            string.Join(", ", packets.Select(p => p.MsgId.ToString().Replace("Msg", "")).Take(24)) +
            (packets.Count > 24 ? ", ..." : ""));
    }

    public void Dispose() => client.Close();
}
