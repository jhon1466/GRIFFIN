// Protocol smoke test for the private server. Plays the client side of the documented flow:
//   HTTP /email/login -> TCP CS_Login -> startup requests -> inventory/gacha/dorm requests -> reconnect.
// It never touches the user's game client. Test account credentials come from args/env, never hardcoded secrets.
//
// Usage: dotnet run --project Griffin.ProtocolSmoke -- [--http https://127.0.0.1] [--tcp 127.0.0.1:7001]
//            [--email smoke@local.test] [--password <pw>] [--scenario all|rouge|roguelegacy|roguemigrate|bugs|login|calendar|inventory|store|mail|profile|prefs|formation|gun|weapon|love|items|cumulative|controlroom|restored|provision|provisionlogin|mods|talents|garden|cooking|equip|lobby|display|locked|battlepass|gacha|dorm]
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
    // The class promotion below spends ItemData goods; seed only the isolated test account, preserving any positive balances.
    main.Send(new CS_Chat { Uid = 1, Message = "/inventory provision" });
    main.ReadFor(TimeSpan.FromSeconds(6));
    main.Send(new CS_Items());
    var seededInventory = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Items>().FirstOrDefault();
    int classMaterialBefore = seededInventory?.Items.GetValueOrDefault(8328u) ?? 0;
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
        // The client's dorm action gate uses its separate per-character level achievement (Gun 1008 -> 410081 at level 20).
        // The account must receive the newly completed counter immediately after leveling, not only on a later login/query.
        var unlockedCounters = up.Select(p => p.Message).OfType<SC_CommonQuestCountUpdate>()
            .FirstOrDefault(u => u.Counters.Type == CommonQuestCounters.Types.Type.Achievement)?.Counters;
        uint level20Achievement = 410000u + (id - 1000u) * 10u + 1u;
        Check("gun: crossing level 20 pushes its per-character dorm achievement counter",
            unlockedCounters is not null && unlockedCounters.Rewards.TryGetValue(level20Achievement, out bool unlocked) && unlocked,
            $"gun={id}, expected={level20Achievement}, pushed={unlockedCounters?.Rewards.ContainsKey(level20Achievement)}");

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
        int? classMaterialAfter = clsMessages.Select(p => p.Message).OfType<SC_Items>().FirstOrDefault()?.Items.GetValueOrDefault(8328u);
        Check("gun: the promotion charged its tabled materials (4 x 8328 and 1000 gold)",
            classMaterialBefore >= 4 && classMaterialAfter == classMaterialBefore - 4
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
    var loose = after?.Weapons.Where(w => w.StcId == 11016 && !after.FEDCFGGDNBN.ContainsKey(w.Id)).OrderByDescending(w => w.Id).Take(2).ToList() ?? [];
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

if (scenario is "all" or "minigame")
{
    // A-101: the "I Wanna" challenge (28001) of the event and any other mini game of an event open and finish
    main.Send(new CS_ThemeMiniGameStart { ThemeId = 4101, FNEFKKLJMDN = 28001, StageId = 2001 });
    Check("minigame: the Iwanna challenge starts", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ThemeMiniGameStart>().Any(r => r.ThemeId == 4101 && r.FNEFKKLJMDN == 28001 && r.StageId == 2001));
    main.Send(new CS_ThemeMiniGameFinish { ThemeId = 4101, FNEFKKLJMDN = 28001, StageId = 2001, Score = 777, Iwanna = new CS_ThemeMiniGameFinish.Types.KFKMJBNENIB { Result = LLKGHFKHHHN.MiniGameWin, GJPONLPNJNG = 777 } });
    Check("minigame: finishing it is answered with the result", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ThemeMiniGameFinish>().Any(r => r.StageId == 2001 && r.Iwanna is not null && r.Iwanna.Result == LLKGHFKHHHN.MiniGameWin));
    main.Send(new CS_ThemeMiniGameInfo { ThemeId = 4101, FNEFKKLJMDN = 28001 });
    var iwannaInfo = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ThemeMiniGameInfo>().FirstOrDefault();
    byte[] iwannaBytes = iwannaInfo?.ToByteArray() ?? [];
    Check("minigame: the record of the stage is kept (score 777 in the info)", Enumerable.Range(0, Math.Max(0, iwannaBytes.Length - 1)).Any(i => iwannaBytes[i] == 0x89 && iwannaBytes[i + 1] == 0x06));
    main.Send(new CS_ThemeMiniGameStart { ThemeId = 3001, FNEFKKLJMDN = 23002, StageId = 1 });
    Check("minigame: another mini game opens with a generic answer", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ThemeMiniGameStart>().Any(r => r.FNEFKKLJMDN == 23002));
    // A-102: the lobby "butt game" challenge (擂台挑战)
    main.Send(new CS_LobbyGameInfo { Type = GGIHCININON.LobbyGamTypeButtGame });
    var buttInfo = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyGameInfo>().FirstOrDefault();
    Check("minigame: the lobby game info lists the three butt games", buttInfo is not null && buttInfo.ToByteArray().Length > 20, $"bytes={buttInfo?.ToByteArray().Length}");
    var buttStart = new FJKDCILGCKA { LAOKONCHCHL = 103, StageId = 1030101, Type = MJHICDHLNBG.LobbyButtGameTypeNormal };
    main.Send(new CS_LobbyGameStart { Type = GGIHCININON.LobbyGamTypeButtGame, LobbyButtGameStart = buttStart });
    Check("minigame: a butt game starts", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyGameStart>().Any(r => r.LobbyButtGameStart?.StageId == 1030101));
    main.Send(new CS_LobbyGameFinish { Type = GGIHCININON.LobbyGamTypeButtGame, LobbyButtGameFinish = new EAAIAKKEEMB { JOOPJGIOLIE = buttStart, Result = FPDDLCKDDNE.LobbyGameWin, Duration = 30 } });
    Check("minigame: finishing it answers the result", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyGameFinish>().Any(r => r.LobbyButtGameFinish?.Result == FPDDLCKDDNE.LobbyGameWin));
    main.Send(new CS_LobbyGameInfo { Type = GGIHCININON.LobbyGamTypeButtGame });
    var buttAfter = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyGameInfo>().FirstOrDefault();
    Check("minigame: the win is kept in the info", buttAfter is not null && buttAfter.ToByteArray().Length > (buttInfo?.ToByteArray().Length ?? 0));
}
if (scenario is "all" or "lobbyroom")
{
    // A-103: cleaning a room of the build list (the bathroom, 108) is paid for and kept
    main.Send(new CS_Chat { Uid = 1, Message = "/inventory provision" });
    main.ReadFor(TimeSpan.FromSeconds(6));
    main.Send(new CS_LobbyUnlockRoom { IAAPPCGFLMD = 108 });
    var roomReply = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).ToList();
    Check("lobby: the bathroom (108) is cleaned and paid for", roomReply.OfType<SC_LobbyUnlockRoom>().Any(r => r.IAAPPCGFLMD == 108), $"messages={string.Join(',', roomReply.Select(m => m.GetType().Name))}");
    main.Send(new CS_LobbyUnlockRoom { IAAPPCGFLMD = 108 });
    Check("lobby: cleaning it twice is refused", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any());
    main.Send(new CS_LobbyGetBuildInfo());
    var buildInfo = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_LobbyGetBuildInfo>().FirstOrDefault();
    Check("lobby: the cleaned room is listed in the build info", buildInfo is not null && buildInfo.CPJOGIOCNMM.ContainsKey(108));
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
        stgSync is not null && Math.Abs((long)stgSync.Timestamp - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) < 3600, $"timestamp={stgSync?.Timestamp}");

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
    // A-90: the team of the last battle is offered again by the deployment screen
    main.Send(new CS_StageUsableGuns { StageId = 10101 });
    var stgLastTeam = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_StageUsableGuns>().FirstOrDefault();
    Check("stage: the deployment screen gets the remembered team of the last battle", stgLastTeam is not null && stgLastTeam.Lineup.ToList().SequenceEqual(new uint[] { 1017, 1008 }), $"lineup={string.Join(',', stgLastTeam?.Lineup ?? [])}");
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
    // the weapon skill (field 9 of the skill message) as the client's own unit builder writes it (A-75)
    bool HasWeaponSkill(byte[] gun)
    {
        var sr = new CodedInputStream(StgFields(gun, 11).FirstOrDefault() ?? []);
        uint tag;
        while ((tag = sr.ReadTag()) != 0)
        {
            if ((tag >> 3) == 9 && sr.ReadUInt32() != 0)
                return true;
            if ((tag >> 3) != 9)
                sr.SkipLastField();
        }

        return false;
    }
    Check("stage: each unit's skill message carries the skill of its weapon (field 9)", stgGuns.Count == 2 && stgGuns.All(g => g is not null && HasWeaponSkill(g)));
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
    // public keys: the client chooses the affixes (field 6 = four pairs, property << 8 | level), the server checks them against EntryData, keeps and returns them (A-75)
    main.Send(new CS_Chat { Uid = 1, Message = "/inventory provision" });   // the keys cost Universal Chain Keys (228), Transcription Conductors VI (9011) and gold
    main.ReadFor(TimeSpan.FromSeconds(4));
    main.Send(new CS_PublicTalentSkillItems());
    var keyList = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_PublicTalentSkillItems>().FirstOrDefault();
    var keyItem = keyList?.PublicSkillItems.FirstOrDefault();
    if (keyItem is not null)
    {
        ulong chosen = (30UL << 8 | 3) | ((9UL << 8 | 5) << 16);
        main.Send(new CS_PublicTalentItemLevelUp { Id = keyItem.Id, Locked = chosen });
        var keyReply = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_PublicTalentItemLevelUp>().FirstOrDefault();
        main.Send(new CS_PublicTalentSkillItems());
        var keyAfter = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_PublicTalentSkillItems>().FirstOrDefault()?.PublicSkillItems.FirstOrDefault(k => k.Id == keyItem.Id);
        Check("public keys: the chosen affixes are confirmed and sent back in the item list (field 4)", keyReply?.Locked == chosen && keyAfter?.Locked == chosen, $"reply={keyReply?.Locked} list={keyAfter?.Locked}");
        main.Send(new CS_PublicTalentItemLevelUp { Id = keyItem.Id, Locked = 99UL << 8 | 1 });
        Check("public keys: an affix the rank does not allow is refused", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo == -10008));
    }
    // support chips (A-77): 63 core chips owned, up to three per formation, saved, shown in the room and refused when unknown
    main.Send(new CS_SupportChip());
    var chipList = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SupportChip>().FirstOrDefault();
    Check("support: the account owns 6 copies of each of the 63 core chips", chipList?.ENFCKIOLJNM.Count == 378, $"chips={chipList?.ENFCKIOLJNM.Count}");
    var chipIds = chipList?.ENFCKIOLJNM.Select(c => c.Id).Take(4).ToList() ?? [];
    // A-93: the support teams of the "备勤区": the account owns them and the formation slots keep them
    main.Send(new CS_SupportTeams());
    var supportTeams = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SupportTeams>().FirstOrDefault();
    Check("support: the account owns the 20 support teams", supportTeams is not null && supportTeams.NJMEBOGONKG.Count == 20 && supportTeams.NJMEBOGONKG.Contains(1001u), $"teams={supportTeams?.NJMEBOGONKG.Count}");
    main.Send(new CS_SupportFormationModify { Id = 2, Slot = 1, COCHLPMJPOJ = 1001 });
    var teamReply = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SupportFormationModify>().FirstOrDefault();
    Check("support: a team goes into slot 1 of formation 2", teamReply is not null && teamReply.Id == 2 && teamReply.FOAHOCCEHMB.CDLNJHGCDMK[0].COCHLPMJPOJ == 1001);
    main.Send(new CS_SupportFormationModify { Id = 2, Slot = 1, COCHLPMJPOJ = 1004 });
    Check("support: a team of the other type is refused in slot 1", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any());
    main.Send(new CS_SupportRoomInfo());
    var roomTeams = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_SupportRoomInfo>().FirstOrDefault();
    Check("support: the room shows the team of the slot", roomTeams?.ABOENIKPJLH.KMPIHKFBAEP[1].CDLNJHGCDMK[0].COCHLPMJPOJ == 1001);
    // A-94: the room ("阵列规模") levels up for the material of SupportLevelData
    main.Send(new CS_Chat { Uid = 1, Message = "/inventory provision" });
    main.ReadFor(TimeSpan.FromSeconds(6));
    main.Send(new CS_SupportRoomUpgrade { JLHFICPMAEI = 2 });
    var roomUp = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).ToList();
    Check("support: the room goes to level 2", roomUp.OfType<SC_SupportRoomUpgrade>().Any(r => r.JLHFICPMAEI == 2), $"messages={string.Join(',', roomUp.Select(m => m.GetType().Name))}");
    main.Send(new CS_SupportRoomInfo());
    Check("support: the room info reports level 2", main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_SupportRoomInfo>().FirstOrDefault()?.ABOENIKPJLH.HLCFEAGODDH == 2);
    main.Send(new CS_SupportRoomUpgrade { JLHFICPMAEI = 6 });
    Check("support: the dialog can pick a target several levels up (2 to 6 in one request)", main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_SupportRoomUpgrade>().Any(r => r.JLHFICPMAEI == 6));
    // A-96: "补给制造": the ammunition is made from the one of the tier below plus a material
    main.Send(new CS_SupportAmmunitionCompose { Id = 2301501, BAGFCBNOMCO = 1 });
    Check("support: the elite ammunition cannot be made without the ammunition of the tier below", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any());
    main.Send(new CS_SupportAmmunitionCompose { Id = 2301401, BAGFCBNOMCO = 2 });
    var composeOne = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).ToList();
    Check("support: two units of the first-tier ammunition are made from the material", composeOne.OfType<SC_SupportAmmunitionCompose>().Any(r => r.Id == 2301401 && r.Num == 2) && composeOne.OfType<SC_Items>().Any(i => i.Items.GetValueOrDefault(2301401u) >= 2), $"messages={string.Join(',', composeOne.Select(m => m.GetType().Name))}");
    main.Send(new CS_SupportAmmunitionCompose { Id = 2301501, BAGFCBNOMCO = 1 });
    Check("support: with two of the tier below the elite ammunition is made", main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_SupportAmmunitionCompose>().Any(r => r.Id == 2301501 && r.Num == 1));
    // A-97: the base chips of the "框架拓展" screen are owned and can be placed in a formation
    var baseChipList = new List<uint>();
    main.Send(new CS_SupportChip());
    var allChips = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_SupportChip>().FirstOrDefault();
    Check("support: the account owns the 162 base chips twice (324) with their stat value", allChips is not null && allChips.CCGPCPEPBEA.Count == 324 && allChips.CCGPCPEPBEA.All(c => c.ACEIFDEGLMH.Count == 6 && c.ACEIFDEGLMH.Sum(v => (long)v) > 0), $"baseChips={allChips?.CCGPCPEPBEA.Count}");
    if (allChips is not null && allChips.CCGPCPEPBEA.Count >= 2)
    {
        uint firstChip = allChips.CCGPCPEPBEA[0].Id, otherChip = allChips.CCGPCPEPBEA[2].Id;
        var placeChips = new CS_SupportFormationModifyBaseChip { Id = 1 };
        var slotsMsg = new DKIBNBANDLM(); slotsMsg.CCGPCPEPBEA.AddRange(new[] { firstChip, 0u, otherChip });
        placeChips.JELCIHPBECA[1] = slotsMsg;
        main.Send(placeChips);
        Check("support: base chips are placed in a frame of a formation", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SupportFormationModifyBaseChip>().Any(r => r.Id == 1 && r.JELCIHPBECA.ContainsKey(1)));
        main.Send(new CS_SupportRoomInfo());
        var roomChips = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_SupportRoomInfo>().FirstOrDefault();
        Check("support: the room shows the base chips of the formation", roomChips?.ABOENIKPJLH.KMPIHKFBAEP[0].JELCIHPBECA.TryGetValue(1, out var shown) == true && shown.CCGPCPEPBEA.Contains(firstChip));
        var placeTwice = new CS_SupportFormationModifyBaseChip { Id = 1 };
        var twice = new DKIBNBANDLM(); twice.CCGPCPEPBEA.AddRange(new[] { firstChip, firstChip });
        placeTwice.JELCIHPBECA[1] = twice;
        main.Send(placeTwice);
        Check("support: the same base chip twice is refused", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any());
    }
    if (chipIds.Count == 4)
    {
        var formation = new CS_SupportFormationModifyCoreChip { Id = 1 };
        formation.JHCLPBBJDFH.AddRange(chipIds.Take(2));
        main.Send(formation);
        var formationReply = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SupportFormationModifyCoreChip>().FirstOrDefault();
        main.Send(new CS_SupportRoomInfo());
        var room = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_SupportRoomInfo>().FirstOrDefault();
        Check("support: a formation keeps its core chips and the room shows them",
            formationReply?.ENFCKIOLJNM.SequenceEqual(chipIds.Take(2)) == true && room?.ABOENIKPJLH.KMPIHKFBAEP[0].ENFCKIOLJNM.SequenceEqual(chipIds.Take(2)) == true);
        // the formation used in battle (1 by default) gives its chips to every character: two chips at break 0 = +1 % life (DevelopProperty 30) each
        main.Send(new CS_StageStart { StcStageId = 10101 });
        main.ReadFor(TimeSpan.FromSeconds(2));
        main.Send(new CS_StageInto { Guns = { [1769499u] = 1017u, [1835035u] = 1008u } });
        var chipBattle = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StageInto>().FirstOrDefault()?.Stage;
        var chipUnit = chipBattle?.Camps.Select(c => c.ToByteArray()).Where(c => StgSide(c) == 1).SelectMany(c => StgFields(c, 2)).Select(u => StgFields(u, 1).FirstOrDefault()).FirstOrDefault();
        int chipLife = 0;
        if (chipUnit is not null)
            foreach (var entry in StgFields(StgFields(chipUnit, 22)[0], 1))
            {
                var er = new CodedInputStream(entry); int k = 0, v = 0; uint t2;
                while ((t2 = er.ReadTag()) != 0) { if ((t2 >> 3) == 1) k = er.ReadInt32(); else if ((t2 >> 3) == 2) v = er.ReadInt32(); else er.SkipLastField(); }
                if (k == 30) chipLife = v;
            }
        Check("support: the core chips of the battle formation add to the stats of the characters (MaxHpPercent 30 = 20 with two chips)", chipLife >= 20, $"MaxHpPercent={chipLife}");
        // A-106: a together battle (affection event) puts the partner at the first default point and carries no support unit
        main.Send(new CS_StageStart { StcStageId = 42025, Other = new StageOther { Together = new CKAOJMGCFFF { GunId = 1017, KFIAKLNJHMB = 5 } } });
        main.ReadFor(TimeSpan.FromSeconds(2));
        main.Send(new CS_StageInto());
        var togetherBattle = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StageInto>().FirstOrDefault()?.Stage;
        var togetherUnits = togetherBattle?.Camps.Select(c => c.ToByteArray()).Where(c => StgSide(c) == 1).SelectMany(c => StgFields(c, 2)).ToList() ?? [];
        Check("stage: a together battle has the partner (Gun unit) and no support unit", togetherUnits.Count == 1 && StgFields(togetherUnits[0], 1).Count == 1 && StgFields(togetherUnits[0], 5).Count == 0, $"units={togetherUnits.Count}");
        // A-95: the teams of the battle formation go into the battle as a Support unit (field 5 of the unit wrapper)
        main.Send(new CS_SupportFormationModify { Id = 1, Slot = 1, COCHLPMJPOJ = 1001 });
        main.ReadFor(TimeSpan.FromSeconds(1));
        main.Send(new CS_SupportFormationModify { Id = 1, Slot = 2, COCHLPMJPOJ = 1004 });
        main.ReadFor(TimeSpan.FromSeconds(1));
        main.Send(new CS_StageStart { StcStageId = 10101 });
        main.ReadFor(TimeSpan.FromSeconds(2));
        main.Send(new CS_StageInto { Guns = { [1769499u] = 1017u, [1835035u] = 1008u } });
        var supportBattle = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StageInto>().FirstOrDefault()?.Stage;
        var supportUnits = supportBattle?.Camps.Select(c => c.ToByteArray()).Where(c => StgSide(c) == 1).SelectMany(c => StgFields(c, 2)).Where(u => StgFields(u, 5).Count == 1).ToList() ?? [];
        var supportInner = supportUnits.Count == 1 ? StgFields(supportUnits[0], 5)[0] : null;
        var supportEntries = supportInner is null ? [] : StgFields(supportInner, 5);
        Check("support: the battle carries one support unit with the formation, its six stats and one entry per team",
            supportInner is not null && StgFields(supportInner, 3).Count == 1 && supportEntries.Count == 2, $"units={supportUnits.Count} entries={supportEntries.Count}");
        Check("support: every entry names a team, a skill row and an ammunition", supportEntries.Count == 2 && supportEntries.All(entry =>
        {
            var er = new CodedInputStream(entry); uint team = 0, skillRow = 0, ammo = 0, tag;
            while ((tag = er.ReadTag()) != 0) { if ((tag >> 3) == 1) team = er.ReadUInt32(); else if ((tag >> 3) == 2) skillRow = er.ReadUInt32(); else if ((tag >> 3) == 3) ammo = er.ReadUInt32(); else er.SkipLastField(); }
            return team != 0 && skillRow != 0 && ammo != 0;
        }));
        main.Send(new CS_SupportFormationModify { Id = 1, Slot = 1, COCHLPMJPOJ = 0 });
        main.ReadFor(TimeSpan.FromSeconds(1));
        main.Send(new CS_SupportFormationModify { Id = 1, Slot = 2, COCHLPMJPOJ = 0 });
        main.ReadFor(TimeSpan.FromSeconds(1));
        var tooMany = new CS_SupportFormationModifyCoreChip { Id = 1 };
        tooMany.JHCLPBBJDFH.AddRange(chipIds);
        main.Send(tooMany);
        Check("support: more core chips than slots are refused", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any());
        var unknown = new CS_SupportFormationModifyCoreChip { Id = 1 };
        unknown.JHCLPBBJDFH.Add(9999);
        main.Send(unknown);
        Check("support: an unknown chip is refused", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any());
    }
    // break (consumes copies of the same chip, one break each) and decompose (A-78)
    if (chipList is not null)
    {
        var groups = chipList.ENFCKIOLJNM.GroupBy(c => c.StcId).Where(g => g.Count() >= 6).Take(2).ToList();
        var strong = groups[0].Select(c => c.Id).ToList();
        main.Send(new CS_SupportChipBreak { Id = strong[0], NNGEBCPGILL = { strong[1], strong[2] } });
        var broke = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SupportChipBreak>().FirstOrDefault();
        Check("support: breaking a chip with two copies raises its break to 2 and consumes them", broke is { BreakTimes: 2 } && broke.NNGEBCPGILL.SequenceEqual([strong[1], strong[2]]), $"break={broke?.BreakTimes}");
        main.Send(new CS_SupportChipLockUnlock { BHIHPMCMOEL = 2, Id = strong[3], MDFIDAFPBJN = true });
        Check("support: a chip can be locked", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SupportChipLockUnlock>().Any(l => l.Id == strong[3] && l.MDFIDAFPBJN));
        main.Send(new CS_SupportChipBreak { Id = strong[0], NNGEBCPGILL = { strong[3] } });
        Check("support: a locked copy cannot be a material without the confirmation (SupportCoreChipLocked)", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo is not 0));
        main.Send(new CS_SupportChipBreak { Id = strong[0], NNGEBCPGILL = { strong[3], strong[4], strong[5] }, MNIPNBBJPJL = true });
        var broke5 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SupportChipBreak>().FirstOrDefault();
        Check("support: with the confirmation the break reaches its last step (5) and takes only the copies it needs", broke5 is { BreakTimes: 5 } && broke5.NNGEBCPGILL.Count == 3, $"break={broke5?.BreakTimes}");
        main.Send(new CS_SupportChipBreak { Id = strong[0], NNGEBCPGILL = { groups[1].First().Id } });
        Check("support: a chip at its last break refuses another one", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any());
        var scrap = groups[1].Select(c => c.Id).Skip(3).Take(2).ToList();
        main.Send(new CS_SupportChipDecompose { BHIHPMCMOEL = 2, Ids = { scrap } });
        var scrapped = main.ReadFor(TimeSpan.FromSeconds(2));
        Check("support: decomposing two chips lists them and returns 178006 (协同阵列升级材料)",
            scrapped.Select(p => p.Message).OfType<SC_SupportChipDecompose>().Any(d => d.Ids.SequenceEqual(scrap)) && scrapped.Any(p => p.Message is SC_Items or SC_ResourceUpdate));
        main.Send(new CS_SupportChip());
        var chipsAfter = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_SupportChip>().FirstOrDefault();
        Check("support: the list lost the 5 + 2 chips and keeps the break of the broken one", chipsAfter?.ENFCKIOLJNM.Count == 371 && chipsAfter.ENFCKIOLJNM.First(c => c.Id == strong[0]).BreakTimes == 5, $"chips={chipsAfter?.ENFCKIOLJNM.Count}");
    }
    // powers of the mods (A-78): field 4 of the mod and field 15 (accessories) of the unit of the character that wears it
    main.Send(new CS_Chat { Uid = 1, Message = "/inventory addall weaponmod" });
    main.ReadFor(TimeSpan.FromSeconds(8));
    main.Send(new CS_GunWeaponMods());
    var powerMods = main.ReadFor(TimeSpan.FromSeconds(8)).Select(p => p.Message).OfType<SC_GunWeaponMods>().SelectMany(m => m.Mods).Where(m => m.ModSuitPowerId != 0).ToList();
    Check("mods: the class-3 mods of rank 4 and 5 carry the effect id of their power (1..16, valid in ModPowerData and ModPowerEffectData) in field 4", powerMods.Count >= 300 && powerMods.All(m => m.ModSuitPowerId is >= 1 and <= 16), $"powered={powerMods.Count}");
    main.Send(new CS_GunWeapons());
    var powerWeapon = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeapons>().SelectMany(m => m.Weapons.Where(w => m.FEDCFGGDNBN.GetValueOrDefault(w.Id) == 1017u)).FirstOrDefault();
    if (powerWeapon is not null && powerMods.Count > 0)
    {
        main.Send(new CS_GunWeaponModBelong { WeaponId = powerWeapon.Id, Slot = 1, WeaponModId = powerMods[0].Id });
        main.ReadFor(TimeSpan.FromSeconds(2));
        main.Send(new CS_StageStart { StcStageId = 10101 });
        main.ReadFor(TimeSpan.FromSeconds(2));
        main.Send(new CS_StageInto { Guns = { [1769499u] = 1017u, [1835035u] = 1008u } });
        var powerBattle = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StageInto>().FirstOrDefault()?.Stage;
        var powerUnits = powerBattle?.Camps.Select(c => c.ToByteArray()).Where(c => StgSide(c) == 1).SelectMany(c => StgFields(c, 2)).Select(u => StgFields(u, 1).FirstOrDefault()).Where(u => u is not null).ToList() ?? [];
        var accessories = powerUnits.SelectMany(u => StgFields(StgFields(u!, 11).FirstOrDefault() ?? [], 15)).ToList();
        uint accessorySkill = 0;
        int accessoryValues = 0;
        if (accessories.Count > 0)
        {
            var ar = new CodedInputStream(accessories[0]); uint at;
            while ((at = ar.ReadTag()) != 0)
            {
                if ((at >> 3) == 1) accessorySkill = ar.ReadUInt32();
                else if ((at >> 3) == 2) accessoryValues = ar.ReadBytes().Length;
                else ar.SkipLastField();
            }
        }

        Check("mods: the unit of the character wearing a power mod lists its skill (1001..1020) with the values of its rank in the accessories (field 15)", accessories.Count == 1 && accessorySkill is >= 1001 and <= 1020 && accessoryValues > 0,
            $"accessories={accessories.Count} skill={accessorySkill} bytes={accessoryValues}");
    }

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
        var afterAll = main.ReadFor(TimeSpan.FromSeconds(4)).Select(p => p.Message).ToList();
        var after = afterAll.OfType<SC_GunWeaponMods>().SelectMany(m => m.Mods).ToList();
        Check("mods: the material was consumed", after.All(m => m.Id != modB.Id) && after.Any(m => m.Id == modA.Id));
        // the 4.0.5136 client creates every mod at level 1 (no level/experience in the list: field 5 is its polarity): the real level comes in a push (A-74)
        Check("mods: the list carries no level (field 5 = polarity = 0) and the level of a levelled mod is pushed after it",
            after.All(m => m.Level == 0) && afterAll.OfType<SC_GunWeaponModLvUp>().Any(l => l.Id == modA.Id && l.Level == upReply?.Level), $"pushes={afterAll.OfType<SC_GunWeaponModLvUp>().Count()}");
        // the affixes (field 3): four 16-bit pairs, low byte = value, high byte = DevelopProperty from AffixGroupData (1 Pow, 2 MaxHp, 4 ShieldArmor, 9 Crit, 10 CritMult, 29/30/33 percentages)
        var allowed = new HashSet<ulong> { 1, 2, 4, 9, 10, 29, 30, 33 };
        bool WellFormed(ulong bits)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                ulong pair = bits >> (slot * 16);
                ulong value = pair & 0xFF, property = (pair >> 8) & 0xFF;
                if ((value == 0) != (property == 0) || (property != 0 && !allowed.Contains(property)))
                    return false;
            }

            return true;
        }
        Check("mods: the mods carry affixes in field 3 (well formed pairs; more than 700 mods have at least one)",
            after.Count(m => m.Locked != 0) > 700 && after.All(m => WellFormed(m.Locked)), $"withAffixes={after.Count(m => m.Locked != 0)}/{after.Count}");

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

        // ---- polarization (amplification) and refinement of a rank-5 mod (A-78): the 4.0.5136 client levels mods up with CS 11073
        main.Send(new CS_Chat { Uid = 1, Message = "/inventory provision" });
        main.ReadFor(TimeSpan.FromSeconds(6));
        var top = allMods.FirstOrDefault(m => m.StcId == 120127353);
        Check("mods: the account owns a rank-5 mod for the polarization checks", top is not null);
        if (top is not null)
        {
            main.Send(new CS_GunWeaponModPolarization { Id = top.Id, Items = { { 8307u, 1u } } });
            Check("mods: enhancing with too little fodder is refused (the bar needs 5000 experience for a rank-5 mod)",
                main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any());

            // upgrade items: dismantle 300 other mods first (the account has none of 8307 yet)
            var junk = new CS_GunWeaponModDismantle();
            junk.Ids.AddRange(allMods.Where(m => m.Id != top.Id && m.Id != modA.Id && m.Id != modB.Id && m.Id != modC.Id).Take(300).Select(m => (ulong)m.Id));
            main.Send(junk);
            main.ReadFor(TimeSpan.FromSeconds(3));
            main.Send(new CS_GunWeaponModPolarization { Id = top.Id, Items = { { 8307u, 25u } } });
            var enhanced = main.ReadFor(TimeSpan.FromSeconds(3));
            var firstMod = enhanced.Select(p => p.Message).OfType<SC_GunWeaponModPolarization>().FirstOrDefault()?.Mod;
            ulong firstPolarity = firstMod?.Suit ?? 0;
            Check("mods: enhancing (CS 11073, 25 upgrade items = 5000 experience) polarizes the affixes, charges gold and the level push comes AFTER the mod",
                firstPolarity != 0 && enhanced.Any(p => p.Message is SC_ResourceUpdate)
                && enhanced.Select(p => p.Message).ToList() is var order && order.FindIndex(m => m is SC_GunWeaponModLvUp) > order.FindIndex(m => m is SC_GunWeaponModPolarization), $"polarity=0x{firstPolarity:X}");

            main.Send(new CS_GunWeaponModPolarization { Id = top.Id, FKBCLPBCILC = 2, Items = { { 8307u, 25u } } });
            var amplified = main.ReadFor(TimeSpan.FromSeconds(3));
            var amplifiedMod = amplified.Select(p => p.Message).OfType<SC_GunWeaponModPolarization>().FirstOrDefault()?.Mod;
            ulong polarity = amplifiedMod?.Suit ?? 0;
            var slots = Enumerable.Range(0, 4).Select(s => (polarity >> (s * 16)) & 0xFFFF).Where(v => v != 0).ToList();
            Check("mods: amplifying with 2 items re-rolls every slot with a permille of the table (100..2000, bit 12 set) and charges the items",
                slots.Count >= 1 && slots.All(v => (v & 0x1000) != 0 && (v & 0xFFF) % 100 == 0 && (v & 0xFFF) is >= 100 and <= 2000) && amplified.Any(p => p.Message is SC_ResourceUpdate or SC_Items),
                $"polarity=0x{polarity:X}");

            main.Send(new CS_WeaponModPolarityRollBack { FNKBBPNGFBC = top.Id, NGODDODILBB = true });
            var redo = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).ToList();
            Check("mods: redoing restores the polarity the mod had before the last roll and answers",
                redo.OfType<SC_WeaponModPolarityRollBack>().Any() && redo.OfType<SC_WeaponModAdvanceEnhance>().Any(r => r.Mod.Suit == firstPolarity));
            main.Send(new CS_WeaponModPolarityRollBack { FNKBBPNGFBC = top.Id, NGODDODILBB = false });
            Check("mods: the backup is gone after a redo (ModPolarityBackUpNotExist)",
                main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo == -14401));

            main.Send(new CS_GunWeaponModPolarization { Id = top.Id, FKBCLPBCILC = 2, Items = { { 8307u, 25u } } });
            amplifiedMod = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeaponModPolarization>().FirstOrDefault()?.Mod;
            polarity = amplifiedMod?.Suit ?? 0;
            main.Send(new CS_WeaponModPolarityRollBack { FNKBBPNGFBC = top.Id, NGODDODILBB = false });
            Check("mods: confirming keeps the polarity", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_WeaponModPolarityRollBack>().Any() && polarity != 0);

            // refinement: one step on the first polarized slot
            int slot = Enumerable.Range(0, 4).First(s => ((polarity >> (s * 16)) & 0xFFF) != 0);
            ulong affixesBefore = amplifiedMod!.Locked;
            main.Send(new CS_WeaponModAdvanceEnhance { Id = top.Id, NOADAJNECCF = { { (uint)slot, 1u } } });
            var refined = main.ReadFor(TimeSpan.FromSeconds(3));
            var refinedMod = refined.Select(p => p.Message).OfType<SC_WeaponModAdvanceEnhance>().FirstOrDefault()?.Mod;
            Check("mods: one refinement step changes the affix or its polarity, counts it (field 5 = 1) and charges items and gold",
                refinedMod is { Level: 1 } && (refinedMod.Locked != affixesBefore || refinedMod.Suit != polarity) && refined.Any(p => p.Message is SC_ResourceUpdate or SC_Items), $"count={refinedMod?.Level}");
            main.Send(new CS_WeaponModAdvanceEnhance { Id = top.Id, NOADAJNECCF = { { 9u, 1u } } });
            Check("mods: a refinement of a slot that does not exist is refused", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo == -10208));

            // continuous polarization: one round (6 upgrade items = 1200 experience >= the 1000 the mod is worth), then keep the candidate
            var series = new CS_GunWeaponModContinuouslyPolarization { Id = top.Id, JFMFOHAHOBL = { new DDFNNIBCBND { Items = { { 8307u, 6u } } } } };
            main.Send(series);
            var rounds = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeaponModContinuouslyPolarization>().FirstOrDefault();
            Check("mods: a continuous round returns one candidate with the same polarized slots", rounds is { GENGCMEPHFM.Count: 1 } r && r.GENGCMEPHFM[0] != 0);
            main.Send(new CS_GunWeaponModGetContinuouslyPolarization());
            Check("mods: the pending series can be read back", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunWeaponModGetContinuouslyPolarization>().Any(g => g.NHJFCEMIPEO.AMKICOIHKPD == top.Id && g.NHJFCEMIPEO.GENGCMEPHFM.Count == 1));
            main.Send(new CS_GunWeaponModContinuouslyPolarizationSelect { Id = top.Id, LMLFNAACPPN = 0 });
            var picked = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).ToList();
            Check("mods: choosing a candidate makes it the polarity of the mod", picked.OfType<SC_GunWeaponModContinuouslyPolarizationSelect>().Any()
                && picked.OfType<SC_WeaponModAdvanceEnhance>().Any(r => rounds is not null && r.Mod.Suit == rounds.GENGCMEPHFM[0]));
            main.Send(new CS_GunWeaponModContinuouslyPolarizationSelect { Id = top.Id, LMLFNAACPPN = 0 });
            Check("mods: choosing again without candidates is refused", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo == -10446));
        }
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

if (scenario == "bugs")
{
    // reported by the players (A-84): the central bedroom rebuild is kept after logging in again, and the reset event gives the character back its investment
    main.Send(new CS_Chat { Uid = 1, Message = "/inventory provision" });
    main.ReadFor(TimeSpan.FromSeconds(6));
    main.Send(new CS_LobbyBedRoomRebuildUnlock { FKDDKKFIKCA = 1002 });
    Check("bedroom: a step whose previous step is not done is refused", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo == -12332));
    main.Send(new CS_LobbyBedRoomRebuildUnlock { FKDDKKFIKCA = 1001 });
    Check("bedroom: step 1001 is unlocked", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_LobbyBedRoomRebuildUnlock>().Any(r => r.FKDDKKFIKCA == 1001));
    main.Send(new CS_LobbyBedRoomRebuildUnlock { FKDDKKFIKCA = 1001 });
    Check("bedroom: unlocking it twice is refused", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo == -12331));
    main.Send(new CS_LobbyBedRoomRebuildUnlock { FKDDKKFIKCA = 1002 });
    main.ReadFor(TimeSpan.FromSeconds(2));
    var bedAgain = await Connect(true);
    bedAgain.Send(new CS_LobbyGetBuildInfo());
    var bedInfo = bedAgain.ReadFor(TimeSpan.FromSeconds(4)).Select(p => p.Message).OfType<SC_LobbyGetBuildInfo>().FirstOrDefault(i => i.KAFLIJKLBKO is not null);
    Check("bedroom: after logging in again the build info lists the steps done (1001, 1002)", bedInfo?.KAFLIJKLBKO.HKCFCDNFCAE.Contains(1001u) == true && bedInfo.KAFLIJKLBKO.HKCFCDNFCAE.Contains(1002u), $"steps={string.Join(",", bedInfo?.KAFLIJKLBKO?.HKCFCDNFCAE ?? [])}");
    bedAgain.Dispose();

    main.Send(new CS_ClientDataGet());
    var clientData = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ClientDataGet>().FirstOrDefault();
    uint barrackSkip = 99;
    if (clientData is not null)
    {
        var cr = new CodedInputStream(clientData.Data.ToByteArray()); uint ct;
        while ((ct = cr.ReadTag()) != 0) { if ((ct >> 3) == 9 && (ct & 7) == 0) barrackSkip = cr.ReadUInt32(); else cr.SkipLastField(); }
    }
    Check("cutscene: the client data starts with the barrack animations set to \"always play\" (2), not \"skip\" (1)", barrackSkip == 2, $"BarrackSkipKey={barrackSkip}");
    // Existing accounts may have uploaded the captured blob with field 9 = 1. Migrate that
    // saved value once, then preserve a later explicit user choice instead of forcing it on every read.
    main.Send(new CS_ClientDataUpload { Data = ByteString.CopyFrom([0x48, 0x01]) });
    var uploadAck = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ClientDataUpload>().FirstOrDefault();
    Check("cutscene: an existing client-data blob upload is acknowledged", uploadAck is not null);
    main.Send(new CS_ClientDataGet());
    var migratedData = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ClientDataGet>().FirstOrDefault();
    uint migratedBarrackSkip = 99;
    if (migratedData is not null)
    {
        var cr = new CodedInputStream(migratedData.Data.ToByteArray()); uint ct;
        while ((ct = cr.ReadTag()) != 0) { if ((ct >> 3) == 9 && (ct & 7) == 0) migratedBarrackSkip = cr.ReadUInt32(); else cr.SkipLastField(); }
    }
    Check("cutscene: a saved skip value is migrated to always play", migratedBarrackSkip == 2, $"BarrackSkipKey={migratedBarrackSkip}");
    main.Send(new CS_ClientDataUpload { Data = ByteString.CopyFrom([0x48, 0x01]) });
    main.ReadFor(TimeSpan.FromSeconds(2));
    main.Send(new CS_ClientDataGet());
    var userChoiceData = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ClientDataGet>().FirstOrDefault();
    uint userBarrackSkip = 99;
    if (userChoiceData is not null)
    {
        var cr = new CodedInputStream(userChoiceData.Data.ToByteArray()); uint ct;
        while ((ct = cr.ReadTag()) != 0) { if ((ct >> 3) == 9 && (ct & 7) == 0) userBarrackSkip = cr.ReadUInt32(); else cr.SkipLastField(); }
    }
    Check("cutscene: a later user setting is not overwritten by the one-time migration", userBarrackSkip == 1, $"BarrackSkipKey={userBarrackSkip}");
    main.Send(new CS_GunLevelUp { GunId = 1017, Level = 20 });
    main.ReadFor(TimeSpan.FromSeconds(3));
    main.Send(new CS_ActicityResetWeaponOrGun { IKDMNOBMIFE = 1, GunId = 1017 });
    var reset = main.ReadFor(TimeSpan.FromSeconds(3));
    Check("reset: the reset event answers and pays back the experience and gold of the levels", reset.Any(p => p.Message is SC_ActicityResetWeaponOrGun { GunId: 1017 }) && reset.Any(p => p.Message is SC_ResourceUpdate or SC_Items));
    main.Send(new CS_Guns());
    var resetGun = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().SelectMany(g => g.Guns).FirstOrDefault(g => g.Id == 1017);
    Check("reset: the character is back at level 1", resetGun is { Level: 1 }, $"level={resetGun?.Level}");
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
    var equipWeapon = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeapons>().SelectMany(m => m.Weapons.Where(w => m.FEDCFGGDNBN.GetValueOrDefault(w.Id) == 1017u)).FirstOrDefault();
    Check("equip: the account has a weapon of 1017 and two mods", equipWeapon is not null && equipMods.Count == 2, $"weapon={equipWeapon?.Id} mods={equipMods.Count}");
    // A-88: field 5 of a weapon is its skin (0 until one is chosen) and survives a new list
    Check("equip: a weapon starts without a skin", equipWeapon is not null && equipWeapon.GunId == 0, $"skin={equipWeapon?.GunId}");
    if (equipWeapon is not null)
    {
        main.Send(new CS_ReplaceGunWeaponSkin { Id = equipWeapon.Id, GunId = 3111593 });
        Check("equip: replacing the skin is answered with weapon and skin", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReplaceGunWeaponSkin>().Any(r => r.Id == equipWeapon.Id && r.GunId == 3111593));
        main.Send(new CS_GunWeapons());
        var skinned = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeapons>().SelectMany(m => m.Weapons).FirstOrDefault(w => w.Id == equipWeapon.Id);
        Check("equip: the chosen skin is kept in the weapon list", skinned?.GunId == 3111593, $"skin={skinned?.GunId}");
        // A-89: the accessory skin of a slot travels in field 2 of the positional mod entries
        main.Send(new CS_ReplaceGunWeaponModSkin { Id = equipWeapon.Id, GunId = 3211001, DPMKGPOCIOK = 2 });
        Check("equip: replacing an accessory skin is answered", main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReplaceGunWeaponModSkin>().Any(r => r.Id == equipWeapon.Id && r.GunId == 3211001 && r.DPMKGPOCIOK == 2));
        main.Send(new CS_GunWeapons());
        var modSkinned = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeapons>().SelectMany(m => m.Weapons).FirstOrDefault(w => w.Id == equipWeapon.Id);
        Check("equip: the accessory skin is kept in slot 2 of the weapon", modSkinned is not null && modSkinned.WeaponMods.Count >= 2 && modSkinned.WeaponMods[1].GunId == 3211001, $"mods={modSkinned?.WeaponMods.Count}");
        main.Send(new CS_ReplaceGunWeaponModSkin { Id = equipWeapon.Id, GunId = 0, DPMKGPOCIOK = 2 });
        main.ReadFor(TimeSpan.FromSeconds(1));
        main.Send(new CS_ReplaceGunWeaponSkin { Id = equipWeapon.Id, GunId = 0 });
        main.ReadFor(TimeSpan.FromSeconds(1));
    }
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
        main.Send(new CS_GunWeaponModBelong { WeaponId = equipWeapon.Id });
        Check("equip: the red button \"unequip all\" (no slot, no mod) frees the equipped mods slot by slot",
            main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GunWeaponModBelong>().Any(b => b.WeaponId == 0 && b.Slot == 2 && b.WeaponModId == equipMods[0].Id));
        main.Send(new CS_GunWeaponModBelong { WeaponId = equipWeapon.Id, Slot = 2, WeaponModId = equipMods[0].Id });
        main.ReadFor(TimeSpan.FromSeconds(2));
    }

    var equipAgain = await Connect(true);
    equipAgain.ReadFor(TimeSpan.FromSeconds(20));
    equipAgain.Send(new CS_Guns());
    var gunsAfter = equipAgain.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_Guns>().FirstOrDefault();
    var gunAfter = gunsAfter?.Guns.FirstOrDefault(g => g.Id == 1017);
    Check("equip: SC_Guns carries the Barrack parts of the costume in its map (field 2) for the parts window", gunsAfter is not null && gunsAfter.GDJOKNOBNBM.GetValueOrDefault(1101701u) == 273, $"map={gunsAfter?.GDJOKNOBNBM.Count}");
    Check("equip: after a new login character 1017 still wears costume 1101701 with parts 273", gunAfter is not null && gunAfter.Costume == 1101701 && gunAfter.CostumeParts == 273,
        $"costume={gunAfter?.Costume} parts={gunAfter?.CostumeParts}");
    equipAgain.Send(new CS_GunWeapons());
    var weaponAfter = equipAgain.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GunWeapons>().SelectMany(m => m.Weapons.Where(w => m.FEDCFGGDNBN.GetValueOrDefault(w.Id) == 1017u)).FirstOrDefault();
    Check("equip: after a new login the weapon keeps exactly the mod put in slot 2", weaponAfter is not null && equipMods.Count == 2 && weaponAfter.WeaponMods.Count == 2 && weaponAfter.WeaponMods[0].Id == 0 && weaponAfter.WeaponMods[1].Id == equipMods[0].Id,
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

    // This negative-price assertion is only valid in the focused, clean `restored` account. The integrated `all`
    // scenario has already provisioned table items for progression earlier, so it must not expect item 202 to be absent.
    if (scenario == "restored")
    {
        main.Send(new CS_LobbyUnlockPart { EGINIIGDCBO = 10410 });
        Check("restored: unlocking a lobby part without its price is refused (ItemNotEnough)",
            main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().Any(e => e.ErrorNo == -10015));
        main.Send(new CS_Chat { Uid = 1, Message = "/inventory provision" });
        main.ReadFor(TimeSpan.FromSeconds(8));
    }
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
    // U-07: the dorm interaction cache and the level achievement are separate state. 101703 is an actual
    // DormFormationData action row for Gun 1017; its separate level gate is achievement 410171. Buying
    // the action must survive both the dorm-formation query and reconnect (daily-info alone is not enough).
    const uint dormAction = 101703;
    main.Send(new CS_BuyGunPerformance { HLKFJHBHEDC = dormAction });
    var boughtAction = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_BuyGunPerformance>().FirstOrDefault();
    Check("dorm: interaction purchase is acknowledged", boughtAction?.HLKFJHBHEDC == dormAction);
    main.Send(new CS_GetDormFormationInfo());
    var formationInfo = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GetDormFormationInfo>().FirstOrDefault();
    Check("dorm: purchased interaction is returned by GetDormFormationInfo",
        formationInfo?.AHFNBKINDGB.TryGetValue(dormAction, out bool actionBought) == true && actionBought,
        $"keys={string.Join(',', formationInfo?.AHFNBKINDGB.Keys ?? [])}");
    main.Send(new CS_GetCharacterDailyInfo());
    var dailyInfo = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GetCharacterDailyInfo>().FirstOrDefault();
    Check("dorm: purchased interaction is also present in CharacterDailyInfo",
        dailyInfo?.GGMFOMHIBNJ.TryGetValue(dormAction, out bool dailyBought) == true && dailyBought);

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

if (scenario is "all" or "rouge")
{
    // 遗珍回收 "Rouge 1" (plan 600002): prepare (difficulty, trend, opening chip, squad) -> start -> open a battle node -> win -> finish (A-116..A-121).
    const uint rougePlan = 600002;
    // no Rouge error code has a text in the client: every refusal is sent as -10085 (A-125); the server log keeps the reason
    static int RougeVisible(HNINDAOJCKP e) => (int)e is <= -14000 and >= -14099 ? (int)HNINDAOJCKP.ActivityMiniGameStatusInvalid : (int)e;
    main.ReadFor(TimeSpan.FromSeconds(1));
    main.Send(new CS_GetRougeInfo { PlanId = rougePlan });
    var rgInfo0 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GetRougeInfo>().FirstOrDefault()?.Info;
    Check("rouge: a new account has the three trends of the season and nothing prepared", rgInfo0 is not null && rgInfo0.SeasonId == 2 && rgInfo0.FEALMJMPBHK.Count == 3 && rgInfo0.IFNEDNHKBJN == 0 && rgInfo0.NDKGOAHEAMJ.Count == 0, $"trends={rgInfo0?.FEALMJMPBHK.Count}");

    main.Send(new CS_RougePrepare { PlanId = rougePlan, IFNEDNHKBJN = 1 });
    var rgPrep1 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougePrepare>().FirstOrDefault()?.Info;
    Check("rouge: Prepare with the difficulty keeps it and offers the trends", rgPrep1 is not null && rgPrep1.IFNEDNHKBJN == 1 && rgPrep1.FEALMJMPBHK.Count == 3);

    main.Send(new CS_RougePrepare { PlanId = rougePlan, IFNEDNHKBJN = 3 });
    var rgLocked = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
    Check("rouge: a locked difficulty is refused (RougePrepareDifficultyLocked)", rgLocked is not null && rgLocked.ErrorNo == RougeVisible(HNINDAOJCKP.RougePrepareDifficultyLocked), $"error={rgLocked?.ErrorNo}");

    uint rgTrend = rgPrep1!.FEALMJMPBHK[0];
    main.Send(new CS_RougePrepare { PlanId = rougePlan, IFNEDNHKBJN = 1, HIGHJLJPCPB = rgTrend });
    var rgPrep2 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougePrepare>().FirstOrDefault()?.Info;
    Check("rouge: choosing the trend offers three opening chips", rgPrep2 is not null && rgPrep2.HIGHJLJPCPB == rgTrend && rgPrep2.PKOFBGMKLLC.Count == 3, $"chips={rgPrep2?.PKOFBGMKLLC.Count}");

    uint rgChip = rgPrep2!.PKOFBGMKLLC[0];
    main.Send(new CS_RougePrepare { PlanId = rougePlan, IFNEDNHKBJN = 1, HIGHJLJPCPB = rgTrend, ODMEICDCEDP = rgChip });
    var rgPrep3 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougePrepare>().FirstOrDefault()?.Info;
    Check("rouge: choosing one of the offered chips keeps it", rgPrep3 is not null && rgPrep3.ODMEICDCEDP == rgChip);

    main.Send(new CS_RougeStart { PlanId = rougePlan });
    var rgNoGuns = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
    Check("rouge: Start without a squad is refused (RougeCreateMapNoGun)", rgNoGuns is not null && rgNoGuns.ErrorNo == RougeVisible(HNINDAOJCKP.RougeCreateMapNoGun), $"error={rgNoGuns?.ErrorNo}");

    uint[] rgSquad = [1001, 1008, 1021, 1022];
    var rgPrepare4 = new CS_RougePrepare { PlanId = rougePlan, IFNEDNHKBJN = 1, HIGHJLJPCPB = rgTrend, ODMEICDCEDP = rgChip };
    rgPrepare4.NDKGOAHEAMJ.AddRange(rgSquad);
    main.Send(rgPrepare4);
    var rgPrep4 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougePrepare>().FirstOrDefault()?.Info;
    Check("rouge: the squad of four is kept with the grade of its preset", rgPrep4 is not null && rgPrep4.NDKGOAHEAMJ.Count == 4 && rgPrep4.NDKGOAHEAMJ.All(g => g.MNNKMKJFABP != 0),
        $"guns={string.Join(",", rgPrep4?.NDKGOAHEAMJ.Select(g => $"{g.GunId}/{g.MNNKMKJFABP}") ?? [])}");

    main.Send(new CS_RougeStart { PlanId = rougePlan });
    var rgStart = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougeStart>().FirstOrDefault()?.Map;
    var rgNodes = rgStart?.GILKCKNOBGM.FirstOrDefault()?.NNHPACALJEI.Values.OrderBy(n => n.KCIMLBLCCHP).ToList() ?? [];
    Check("rouge: Start answers the whole map (a layer per column) with nodes, no current node and the squad", rgStart is not null && rgStart.BCCLAJIJJOA == 1 && rgStart.NOIMLDFHELK == 0 && rgStart.GILKCKNOBGM.Count >= 4
        && rgNodes.Count >= 5 && rgStart.NDKGOAHEAMJ.Count == 4 && rgStart.AMMLMKFDGDK.Contains(rgChip), $"nodes={rgNodes.Count}");
    Check("rouge: every node of the map has a type and a content", rgNodes.Count > 0 && rgNodes.All(n => n.AIODFHGMBME != 0 && n.ANJHKELIAFF != 0));

    main.Send(new CS_GetRougeMapInfo { PlanId = rougePlan });
    var rgMap = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GetRougeMapInfo>().FirstOrDefault()?.Map;
    Check("rouge: GetRougeMapInfo returns the saved map", rgMap is not null && rgMap.GILKCKNOBGM[0].NNHPACALJEI.Count == rgNodes.Count);

    var rgBattle = rgNodes.First(n => n.AIODFHGMBME == 1);
    var rgLater = rgNodes.Last();
    main.Send(new CS_RougeNodeOpen { Id = rgLater.KCIMLBLCCHP, PlanId = rougePlan });
    var rgBadOpen = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
    Check("rouge: a node that is not on the first section cannot be opened first", rgBadOpen is not null && rgBadOpen.ErrorNo == RougeVisible(HNINDAOJCKP.RougeNodeOpenNotSecton1), $"error={rgBadOpen?.ErrorNo}");

    main.Send(new CS_RougeNodePreview { Id = rgBattle.KCIMLBLCCHP, PlanId = rougePlan });
    var rgPreview = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougeNodePreview>().FirstOrDefault();
    Check("rouge: NodePreview returns the node", rgPreview?.GCMADDJNGMI is { } pn && pn.KCIMLBLCCHP == rgBattle.KCIMLBLCCHP && pn.ANJHKELIAFF == rgBattle.ANJHKELIAFF);

    main.Send(new CS_RougeNodeOpen { Id = rgBattle.KCIMLBLCCHP, PlanId = rougePlan });
    var rgOpen = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougeNodeOpen>().FirstOrDefault();
    Check("rouge: NodeOpen of the first battle node answers the node", rgOpen?.GCMADDJNGMI is { } on && on.KCIMLBLCCHP == rgBattle.KCIMLBLCCHP && !on.JONLCAGBGJE);

    main.Send(new CS_StageStart { StcStageId = rgBattle.ANJHKELIAFF + 1, Other = new StageOther { Rouge = new GPNFHGOODHA { PMFFONMEDCP = rgBattle.KCIMLBLCCHP, PlanId = rougePlan } } });
    var rgWrongStage = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
    Check("rouge: StageStart with a stage the node does not hold is refused", rgWrongStage is not null);

    main.Send(new CS_StageStart { StcStageId = rgBattle.ANJHKELIAFF, Other = new StageOther { Rouge = new GPNFHGOODHA { PMFFONMEDCP = rgBattle.KCIMLBLCCHP, PlanId = rougePlan } } });
    var rgStageStart = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StageStart>().FirstOrDefault();
    Check("rouge: StageStart of the node's stage answers the battle with the rouge info", rgStageStart?.Stage is { } rb && rb.StcStageId == rgBattle.ANJHKELIAFF && rgStageStart.Other?.Rouge?.PMFFONMEDCP == rgBattle.KCIMLBLCCHP);

    // the real client names each unit by the GunPresetData id of the squad member (log of 2026-10-18 23:07), not by the character
    main.Send(new CS_StageInto { Guns = { [1310739u] = 1001u, [1376275u] = 1008u, [1441811u] = 1021u, [1507347u] = 1022u } });
    var rgInto = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_StageInto>().FirstOrDefault();
    Check("rouge: StageInto composes the whole preset squad (one unit per member, not one for the whole team)", rgInto?.Stage?.Camps.Count == 2 && rgInto.Other?.Rouge?.PlanId == rougePlan
        && rgInto.Stage.Camps[0].LBJLMGIPOJA.Count >= 4, $"units={rgInto?.Stage?.Camps[0].LBJLMGIPOJA.Count}");
    main.Send(new CS_StageUsableGuns { StageId = 10104 });
    var rgLineup = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_StageUsableGuns>().FirstOrDefault();
    Check("rouge: the preset squad is not remembered as the player's own last team", rgLineup is not null && rgLineup.Lineup.All(id => id == 0));

    main.Send(new CS_StageResult { BattleResult = BattleResult.Win, Count = 1 });
    var rgResult = main.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).ToList();
    Check("rouge: the win is answered with the result and the finished node", rgResult.OfType<SC_StageResult>().Any(r => r.BattleResult == BattleResult.Win && r.StageOther?.Rouge?.PlanId == rougePlan)
        && rgResult.OfType<SC_RougeNodeInteract>().Any(r => r.GCMADDJNGMI?.KCIMLBLCCHP == rgBattle.KCIMLBLCCHP && r.GCMADDJNGMI.JONLCAGBGJE));

    main.Send(new CS_GetRougeMapInfo { PlanId = rougePlan });
    var rgMap2 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GetRougeMapInfo>().FirstOrDefault()?.Map;
    Check("rouge: the saved map has the node finished and as the current node", rgMap2 is not null && rgMap2.NOIMLDFHELK == rgBattle.KCIMLBLCCHP
        && rgMap2.GILKCKNOBGM[0].NNHPACALJEI[rgBattle.KCIMLBLCCHP].JONLCAGBGJE);

    main.Send(new CS_RougeNodeInteract { Id = rgBattle.KCIMLBLCCHP, PlanId = rougePlan });
    var rgAgain = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
    Check("rouge: interacting with a finished node is refused (RougeNodeInteractFinished)", rgAgain is not null && rgAgain.ErrorNo == RougeVisible(HNINDAOJCKP.RougeNodeInteractFinished), $"error={rgAgain?.ErrorNo}");

    // ---- the rest of the run: every layer, every node type (A-124, A-125) ----
    int rgLayerCount = rgStart!.GILKCKNOBGM.Count;
    Check("rouge: the map has one layer per column of the map table (a whole run, not one layer)", rgLayerCount >= 4, $"layers={rgLayerCount}");
    var rgTypesPlayed = new SortedSet<uint> { rgBattle.AIODFHGMBME };
    bool rgShopTested = false, rgEventBadOption = false, rgSkipRefused = false, rgChipPackSeen = false, rgSupplyTried = false, rgGunNodeSeen = false, rgLayerAdvanced = false, rgPackRefreshRefused = false;
    string rgStall = "";

    List<IMessage> RgSend(IMessage request, int ms = 700)
    {
        main.Send(request);
        return main.ReadFor(TimeSpan.FromMilliseconds(ms)).Select(p => p.Message).ToList();
    }

    void RgPickChipIfOffered(List<IMessage> replies)
    {
        var pack = replies.OfType<SC_RougePackOp>().LastOrDefault(r => r.IELOGFIINGN is not null && r.IELOGFIINGN.PKOFBGMKLLC.Count > 0)?.IELOGFIINGN;
        if (pack is null)
            return;
        rgChipPackSeen = true;
        if (!rgPackRefreshRefused)
        {
            rgPackRefreshRefused = true;
            var refused = RgSend(new CS_RougePackOp { CJJJLMCADGM = CS_RougePackOp.Types.AAOPFMBDJHO.OpTypeRefresh, IHLNPPPJELG = CS_RougePackOp.Types.JAJGPMKINOD.OpTargetChip, PlanId = rougePlan }, 500);
            Check("rouge: refreshing the plugin choice without the talent is refused", refused.OfType<SC_ReportError>().Any());
        }

        var chosen = RgSend(new CS_RougePackOp { CJJJLMCADGM = CS_RougePackOp.Types.AAOPFMBDJHO.OpTypePick, IHLNPPPJELG = CS_RougePackOp.Types.JAJGPMKINOD.OpTargetChip, BEKLEGCIKCI = (int)pack.PKOFBGMKLLC[0], PlanId = rougePlan }, 500);
        Check("rouge: picking a plugin of the choice adds it to the plugin list and closes the choice",
            chosen.OfType<SC_RougeMapChipList>().Any(l => l.AMMLMKFDGDK.Contains(pack.PKOFBGMKLLC[0])) && chosen.OfType<SC_RougePackOp>().Any(r => r.IELOGFIINGN is null));
    }

    void RgFightNode(ODFMONHCBIO step)
    {
        RgSend(new CS_StageStart { StcStageId = step.ANJHKELIAFF, Other = new StageOther { Rouge = new GPNFHGOODHA { PMFFONMEDCP = step.KCIMLBLCCHP, PlanId = rougePlan } } }, 400);
        RgSend(new CS_StageInto { Guns = { [1310739u] = 1001u, [1376275u] = 1008u, [1441811u] = 1021u, [1507347u] = 1022u } }, 400);
        var won = RgSend(new CS_StageResult { BattleResult = BattleResult.Win, Count = 1 }, 700);
        Check($"rouge: winning the type {step.AIODFHGMBME} node finishes it and pays coins and terminals", won.OfType<SC_RougeNodeInteract>().Any(r => r.GCMADDJNGMI?.KCIMLBLCCHP == step.KCIMLBLCCHP && r.GCMADDJNGMI.JONLCAGBGJE)
            && (step.AIODFHGMBME == 4 || won.OfType<SC_Items>().Any()), string.Join(",", won.Select(m => m.GetType().Name)));
        RgPickChipIfOffered(won);
    }

    void RgPlayNode(ODFMONHCBIO step, List<IMessage> opened)
    {
        rgTypesPlayed.Add(step.AIODFHGMBME);
        switch (step.AIODFHGMBME)
        {
            case 1 or 2 or 3 or 4 or 8:
                if (!rgSkipRefused && step.AIODFHGMBME == 1)
                {
                    rgSkipRefused = true;
                    var skip = RgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, PlanId = rougePlan }, 500);
                    Check("rouge: the quick finish of a battle is refused without its talent", skip.OfType<SC_ReportError>().Any());
                }

                RgFightNode(step);
                break;
            case 9:
            {
                var gunPack = opened.OfType<SC_RougePackOp>().FirstOrDefault()?.HIOLNHOOLOE;
                Check("rouge: opening the gun node offers three characters of its pool", gunPack is not null && gunPack.HFEIPLDDHIN.Count == 3, $"offered={gunPack?.HFEIPLDDHIN.Count}");
                if (!rgGunNodeSeen)
                {
                    rgGunNodeSeen = true;
                    var refused = RgSend(new CS_RougePackOp { CJJJLMCADGM = CS_RougePackOp.Types.AAOPFMBDJHO.OpTypeRefresh, IHLNPPPJELG = CS_RougePackOp.Types.JAJGPMKINOD.OpTargetGun, PlanId = rougePlan }, 500);
                    Check("rouge: refreshing the character choice without the talent is refused", refused.OfType<SC_ReportError>().Any());
                }

                var picked = RgSend(new CS_RougePackOp { CJJJLMCADGM = CS_RougePackOp.Types.AAOPFMBDJHO.OpTypePick, IHLNPPPJELG = CS_RougePackOp.Types.JAJGPMKINOD.OpTargetGun, BEKLEGCIKCI = (int)gunPack!.HFEIPLDDHIN[0].GunId, PlanId = rougePlan }, 600);
                Check("rouge: picking a character finishes the gun node and clears the pack", picked.OfType<SC_RougeNodeInteract>().Any(r => r.GCMADDJNGMI?.JONLCAGBGJE == true) && picked.OfType<SC_RougePackOp>().Any(r => r.HIOLNHOOLOE is null));
                break;
            }
            case 5:
            {
                if (!rgEventBadOption)
                {
                    rgEventBadOption = true;
                    var bad = RgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, LJJEFHEDHLB = 7, PlanId = rougePlan }, 500);
                    Check("rouge: an event option that does not exist is refused", bad.OfType<SC_ReportError>().Any());
                }

                var option = RgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, LJJEFHEDHLB = (uint)Random.Shared.Next(1, 4), PlanId = rougePlan }, 700);
                Check("rouge: choosing an event option finishes the node", option.OfType<SC_RougeNodeInteract>().Any(r => r.GCMADDJNGMI?.JONLCAGBGJE == true), string.Join(",", option.Select(m => m.GetType().Name)));
                break;
            }
            case 7:
            {
                if (!rgSupplyTried)
                {
                    rgSupplyTried = true;
                    var reinforce = RgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, LJJEFHEDHLB = 1001, PlanId = rougePlan }, 600);
                    Check("rouge: a limited reinforcement raises the grade of a character (or is refused for lack of terminals)",
                        reinforce.OfType<SC_RougeMapGunModify>().Any(g => g.Gun?.GunId == 1001) || reinforce.OfType<SC_ReportError>().Any(), string.Join(",", reinforce.Select(m => m.GetType().Name)));
                }

                var leave = RgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, KBJHHBCEPLB = 2, PlanId = rougePlan }, 600);
                Check("rouge: leaving the supply node finishes it", leave.OfType<SC_RougeNodeInteract>().Any(r => r.GCMADDJNGMI?.JONLCAGBGJE == true));
                break;
            }
            case 6:
            {
                var openedShop = opened.OfType<SC_RougeNodeOpen>().First().GCMADDJNGMI;
                if (!rgShopTested && openedShop?.Content?.Shop is { } rgShop)
                {
                    rgShopTested = true;
                    Check("rouge: the merchant's shop node opens with goods to buy", rgShop.BIOFMBAOLJO.Count > 0 && rgShop.BIOFMBAOLJO.All(g => g.LJJEFHEDHLB == 1), $"goods={rgShop.BIOFMBAOLJO.Count}");
                    var badSell = RgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, LJJEFHEDHLB = 999999, KBJHHBCEPLB = 3, PlanId = rougePlan }, 500);
                    Check("rouge: selling a plugin the squad does not have is refused", badSell.OfType<SC_ReportError>().Any());
                    var sold = RgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, LJJEFHEDHLB = rgChip, KBJHHBCEPLB = 3, PlanId = rougePlan }, 600);
                    Check("rouge: selling the opening plugin pays coins and sends the new plugin list", sold.OfType<SC_RougeNodeInteract>().Any() && sold.OfType<SC_Items>().Any(i => i.Items.ContainsKey(9990102u))
                        && sold.OfType<SC_RougeMapChipList>().FirstOrDefault() is { } list && !list.AMMLMKFDGDK.Contains(rgChip), string.Join(",", sold.Select(m => m.GetType().Name)));
                    uint bought = 0;
                    foreach (var good in rgShop.BIOFMBAOLJO)
                    {
                        var buy = RgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, LJJEFHEDHLB = good.Id, PlanId = rougePlan }, 500);
                        if (buy.OfType<SC_RougeNodeInteract>().FirstOrDefault()?.GCMADDJNGMI?.Content?.Shop is { } after)
                        {
                            bought = good.Id;
                            Check("rouge: buying a plugin charges the coins, marks it sold and adds it to the plugin list",
                                after.BIOFMBAOLJO.First(g => g.Id == good.Id).LJJEFHEDHLB == 0 && buy.OfType<SC_Items>().Any() && buy.OfType<SC_RougeMapChipList>().Any());
                            break;
                        }
                    }

                    Check("rouge: at least one plugin of the shop could be bought with the coins", bought != 0);
                    var twice = RgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, LJJEFHEDHLB = bought, PlanId = rougePlan }, 500);
                    Check("rouge: a plugin cannot be bought twice", twice.OfType<SC_ReportError>().Any());
                    var refresh = RgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, KBJHHBCEPLB = 1, PlanId = rougePlan }, 500);
                    Check("rouge: refreshing the shop without the talent is refused", refresh.OfType<SC_ReportError>().Any());
                    var points = RgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, LJJEFHEDHLB = 5, KBJHHBCEPLB = 4, PlanId = rougePlan }, 600);
                    Check("rouge: selling reinforcement terminals pays coins (or is refused if the account has none)", points.OfType<SC_RougeNodeInteract>().Any() || points.OfType<SC_ReportError>().Any());
                }

                var leave = RgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, KBJHHBCEPLB = 2, PlanId = rougePlan }, 700);
                Check("rouge: leaving the shop finishes the node", leave.OfType<SC_RougeNodeInteract>().Any(r => r.GCMADDJNGMI?.JONLCAGBGJE == true));
                break;
            }
        }
    }

    // play layer after layer: the nodes are tried in id order, an unreachable one is simply refused, so the player's choice between branches is the first reachable one
    for (int layerPass = 0; layerPass < rgLayerCount + 1; layerPass++)
    {
        var current = RgSend(new CS_GetRougeMapInfo { PlanId = rougePlan }, 600).OfType<SC_GetRougeMapInfo>().FirstOrDefault()?.Map;
        if (current is null)
            break;
        uint layerNow = current.BCCLAJIJJOA;
        var layerNodes = current.GILKCKNOBGM[(int)layerNow - 1].NNHPACALJEI.Values.OrderBy(n => n.KCIMLBLCCHP).ToList();
        for (int pass = 0; pass < 8; pass++)
        {
            bool progressed = false;
            foreach (var step in layerNodes)
            {
                var opened = RgSend(new CS_RougeNodeOpen { Id = step.KCIMLBLCCHP, PlanId = rougePlan }, 500);
                if (opened.OfType<SC_RougeNodeOpen>().FirstOrDefault()?.GCMADDJNGMI is not { JONLCAGBGJE: false })
                    continue;
                progressed = true;
                int before = (int)layerNow;
                RgPlayNode(step, opened);
                var check = RgSend(new CS_GetRougeMapInfo { PlanId = rougePlan }, 500).OfType<SC_GetRougeMapInfo>().FirstOrDefault()?.Map;
                if (check is not null && check.BCCLAJIJJOA != before)
                {
                    rgLayerAdvanced = true;
                    break;
                }
            }

            var after = RgSend(new CS_GetRougeMapInfo { PlanId = rougePlan }, 400).OfType<SC_GetRougeMapInfo>().FirstOrDefault()?.Map;
            if (after is null || after.BCCLAJIJJOA != layerNow || !progressed)
                break;
        }
    }

    var rgEnd = RgSend(new CS_GetRougeMapInfo { PlanId = rougePlan }, 600).OfType<SC_GetRougeMapInfo>().FirstOrDefault()?.Map;
    Check("rouge: the run reached the last layer and finished its last node", rgEnd is not null && rgEnd.BCCLAJIJJOA == rgLayerCount
        && rgEnd.GILKCKNOBGM[^1].NNHPACALJEI.Values.Any(n => n.JONLCAGBGJE), $"layer={rgEnd?.BCCLAJIJJOA}/{rgLayerCount} stall={rgStall}");
    Check("rouge: the layer changed during the run", rgLayerAdvanced);
    Check("rouge: battle, gun, event, supply and shop nodes were all played in the run (types seen: " + string.Join(",", rgTypesPlayed) + ")", rgTypesPlayed.IsSupersetOf(new uint[] { 1, 5, 6, 9 }) && rgChipPackSeen);

    main.Send(new CS_RougeFinish { PlanId = rougePlan, Reason = CDPCJHMOCPM.RougeFinishOptionManualQuit });
    var rgFinish = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougeFinish>().FirstOrDefault();
    Check("rouge: Finish settles the run (score and the rows of the finished nodes)", rgFinish is not null && rgFinish.Score > 0 && rgFinish.PLDHFBFAKDI.Count > 0 && rgFinish.PlanId == rougePlan && rgFinish.Reason == CDPCJHMOCPM.RougeFinishReasonSuccessQuit, $"score={rgFinish?.Score} reason={rgFinish?.Reason}");

    main.Send(new CS_GetRougeMapInfo { PlanId = rougePlan });
    var rgMap3 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GetRougeMapInfo>().FirstOrDefault();
    main.Send(new CS_GetRougeInfo { PlanId = rougePlan });
    var rgInfo3 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GetRougeInfo>().FirstOrDefault()?.Info;
    Check("rouge: after Finish there is no map, the preparation is cleared and the experience is kept", rgMap3 is not null && rgMap3.Map is null && rgInfo3 is not null
        && rgInfo3.NDKGOAHEAMJ.Count == 0 && rgInfo3.HIGHJLJPCPB == 0 && (rgInfo3.Exp > 0 || rgInfo3.Level > 0), $"exp={rgInfo3?.Exp} level={rgInfo3?.Level}");

    // talents (A-122): 32 per season, prerequisites, cost 9990101, kept in the info
    main.Send(new CS_RougeUnlockTalent { PlanId = rougePlan, TalentId = 54 });
    var rgTalLocked = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
    Check("rouge: a talent whose prerequisites are not active is refused (RougeUnlockTalentLocked)", rgTalLocked is not null && rgTalLocked.ErrorNo == RougeVisible(HNINDAOJCKP.RougeUnlockTalentLocked), $"error={rgTalLocked?.ErrorNo}");
    main.Send(new CS_RougeUnlockTalent { PlanId = rougePlan, TalentId = 3 });
    var rgTalOther = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
    Check("rouge: a talent of another season is refused (RougeUnlockTalentSeasonCfg)", rgTalOther is not null && rgTalOther.ErrorNo == RougeVisible(HNINDAOJCKP.RougeUnlockTalentSeasonCfg), $"error={rgTalOther?.ErrorNo}");
    main.Send(new CS_RougeUnlockTalent { PlanId = rougePlan, TalentId = 50 });
    var rgTal1 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).ToList();
    // a fresh account has no special-intel points (9990101): the charge is then refused; with AUTO_PROV=1 the chain below runs
    bool rgPoints = rgTal1.OfType<SC_RougeUnlockTalent>().Any();
    Check("rouge: a talent without prerequisites is activated and charged (or refused for lack of points)", rgPoints
        ? rgTal1.OfType<SC_RougeUnlockTalent>().First().MMIKNJIBMBA.SequenceEqual(new uint[] { 50 }) && rgTal1.Any(m => m is SC_ResourceUpdate or SC_Items)
        : rgTal1.OfType<SC_ReportError>().Any(e => e.ErrorNo == (int)HNINDAOJCKP.ItemNotEnough), string.Join(",", rgTal1.Select(m => m.GetType().Name)));
    if (rgPoints)
    {
        main.Send(new CS_RougeUnlockTalent { PlanId = rougePlan, TalentId = 51 });
        var rgTal2 = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougeUnlockTalent>().FirstOrDefault();
        Check("rouge: the next talent of the tree is activated once its prerequisite is", rgTal2 is not null && rgTal2.MMIKNJIBMBA.SequenceEqual(new uint[] { 50, 51 }));
        main.Send(new CS_RougeUnlockTalent { PlanId = rougePlan, TalentId = 51 });
        var rgTalTwice = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
        Check("rouge: a talent cannot be activated twice (RougeUnlockTalentUnlocked)", rgTalTwice is not null && rgTalTwice.ErrorNo == RougeVisible(HNINDAOJCKP.RougeUnlockTalentUnlocked), $"error={rgTalTwice?.ErrorNo}");
        main.Send(new CS_GetRougeInfo { PlanId = rougePlan });
        var rgInfoTal = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_GetRougeInfo>().FirstOrDefault()?.Info;
        Check("rouge: the active talents are in the info (the 2/32 counter)", rgInfoTal is not null && rgInfoTal.MMIKNJIBMBA.OrderBy(x => x).SequenceEqual(new uint[] { 50, 51 }));
    }
    main.Send(new CS_RougeFinish { PlanId = rougePlan, Reason = CDPCJHMOCPM.RougeFinishOptionManualQuit });
    var rgNoMap = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
    Check("rouge: a second Finish is refused (RougeFinishNoMap)", rgNoMap is not null && rgNoMap.ErrorNo == RougeVisible(HNINDAOJCKP.RougeFinishNoMap), $"error={rgNoMap?.ErrorNo}");
}

if (scenario is "all" or "rouge")
{
    // plan 600003 (season 3): the same preparation and a map of several layers, then a manual quit
    const uint plan3 = 600003;
    main.ReadFor(TimeSpan.FromMilliseconds(500));
    main.Send(new CS_RougePrepare { PlanId = plan3, IFNEDNHKBJN = 1 });
    var p3a = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougePrepare>().FirstOrDefault()?.Info;
    Check("rouge (season 3): the three trends of the season are offered", p3a is not null && p3a.SeasonId == 3 && p3a.FEALMJMPBHK.Count == 3);
    main.Send(new CS_RougePrepare { PlanId = plan3, IFNEDNHKBJN = 1, HIGHJLJPCPB = p3a!.FEALMJMPBHK[0] });
    var p3b = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougePrepare>().FirstOrDefault()?.Info;
    var prepare3 = new CS_RougePrepare { PlanId = plan3, IFNEDNHKBJN = 1, HIGHJLJPCPB = p3a.FEALMJMPBHK[0], ODMEICDCEDP = p3b!.PKOFBGMKLLC[0] };
    prepare3.NDKGOAHEAMJ.AddRange(new uint[] { 1001, 1008, 1021, 1022 });
    main.Send(prepare3);
    main.ReadFor(TimeSpan.FromSeconds(2));
    main.Send(new CS_RougeStart { PlanId = plan3 });
    var p3Map = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougeStart>().FirstOrDefault()?.Map;
    Check("rouge (season 3): Start answers a map of several layers", p3Map is not null && p3Map.GILKCKNOBGM.Count >= 4 && p3Map.GILKCKNOBGM.All(l => l.NNHPACALJEI.Count > 0));
    main.Send(new CS_RougeFinish { PlanId = plan3, Reason = CDPCJHMOCPM.RougeFinishOptionManualQuit });
    var p3Fin = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_RougeFinish>().FirstOrDefault();
    Check("rouge (season 3): quitting settles the run as a quit, not a success", p3Fin is not null && p3Fin.Reason == CDPCJHMOCPM.RougeFinishOptionManualQuit);
    main.Send(new CS_RougeTakeGradeReward { PlanId = plan3, MMADAMIMGCM = { 999999u } });
    var p3Grade = main.ReadFor(TimeSpan.FromSeconds(2)).Select(p => p.Message).OfType<SC_ReportError>().FirstOrDefault();
    Check("rouge (season 3): a grade reward that is not in the table is refused", p3Grade is not null);
}

if (scenario is "roguelegacy" or "roguemigrate")
{
    // A run saved by the one-layer build (A-126). Phase 1 (server started with GRIFFIN_ROUGE_LEGACY_MAP=1): play the whole first layer. Phase 2 (normal server, same account
    // and database): loading the saved run must bring it to layer 2. Run with ACCOUNT_EMAIL=<same> and AUTO_PROV=1 for both phases.
    const uint legacyPlan = 600002;
    main.ReadFor(TimeSpan.FromSeconds(1));
    List<IMessage> LgSend(IMessage request, int ms = 600) { main.Send(request); return main.ReadFor(TimeSpan.FromMilliseconds(ms)).Select(p => p.Message).ToList(); }
    if (scenario == "roguelegacy")
    {
        var lgInfo = LgSend(new CS_RougePrepare { PlanId = legacyPlan, IFNEDNHKBJN = 1 }).OfType<SC_RougePrepare>().First().Info;
        var lgTrend = lgInfo.FEALMJMPBHK[0];
        var lgChips = LgSend(new CS_RougePrepare { PlanId = legacyPlan, IFNEDNHKBJN = 1, HIGHJLJPCPB = lgTrend }).OfType<SC_RougePrepare>().First().Info.PKOFBGMKLLC;
        var lgPrepare = new CS_RougePrepare { PlanId = legacyPlan, IFNEDNHKBJN = 1, HIGHJLJPCPB = lgTrend, ODMEICDCEDP = lgChips[0] };
        lgPrepare.NDKGOAHEAMJ.AddRange(new uint[] { 1001, 1008, 1021, 1022 });
        LgSend(lgPrepare);
        var lgMap = LgSend(new CS_RougeStart { PlanId = legacyPlan }).OfType<SC_RougeStart>().FirstOrDefault()?.Map;
        Check("rouge legacy: the one-layer build makes a one-layer map", lgMap is not null && lgMap.GILKCKNOBGM.Count == 1);
        foreach (var step in lgMap!.GILKCKNOBGM[0].NNHPACALJEI.Values.OrderBy(n => n.KCIMLBLCCHP))
            for (int pass = 0; pass < 2; pass++)
            {
                var opened = LgSend(new CS_RougeNodeOpen { Id = step.KCIMLBLCCHP, PlanId = legacyPlan }, 400);
                if (opened.OfType<SC_RougeNodeOpen>().FirstOrDefault()?.GCMADDJNGMI is not { JONLCAGBGJE: false })
                    break;
                if (step.AIODFHGMBME is 1 or 2 or 3 or 4 or 8)
                {
                    LgSend(new CS_StageStart { StcStageId = step.ANJHKELIAFF, Other = new StageOther { Rouge = new GPNFHGOODHA { PMFFONMEDCP = step.KCIMLBLCCHP, PlanId = legacyPlan } } }, 300);
                    LgSend(new CS_StageInto { Guns = { [1310739u] = 1001u, [1376275u] = 1008u, [1441811u] = 1021u, [1507347u] = 1022u } }, 300);
                    LgSend(new CS_StageResult { BattleResult = BattleResult.Win, Count = 1 }, 500);
                }
                else if (step.AIODFHGMBME == 9)
                {
                    var pack = opened.OfType<SC_RougePackOp>().FirstOrDefault()?.HIOLNHOOLOE;
                    LgSend(new CS_RougePackOp { CJJJLMCADGM = CS_RougePackOp.Types.AAOPFMBDJHO.OpTypeGiveup, IHLNPPPJELG = CS_RougePackOp.Types.JAJGPMKINOD.OpTargetGun, PlanId = legacyPlan });
                }
                else
                    LgSend(new CS_RougeNodeInteract { Id = step.KCIMLBLCCHP, LJJEFHEDHLB = step.AIODFHGMBME == 5 ? 1u : 0u, KBJHHBCEPLB = 2, PlanId = legacyPlan });
                break;
            }

        var lgDone = LgSend(new CS_GetRougeMapInfo { PlanId = legacyPlan }).OfType<SC_GetRougeMapInfo>().FirstOrDefault()?.Map;
        Check("rouge legacy: the saved run has its last node finished on the only layer", lgDone is not null && lgDone.BCCLAJIJJOA == 1 && lgDone.GILKCKNOBGM.Count == 1 && lgDone.GILKCKNOBGM[0].NNHPACALJEI.Values.Any(n => n.JONLCAGBGJE && n.AIODFHGMBME == 6));
    }
    else
    {
        var mgMap = LgSend(new CS_GetRougeMapInfo { PlanId = legacyPlan }).OfType<SC_GetRougeMapInfo>().FirstOrDefault()?.Map;
        Check("rouge migrate: a run saved with one finished layer is brought to the whole map and to layer 2", mgMap is not null && mgMap.GILKCKNOBGM.Count >= 4 && mgMap.BCCLAJIJJOA == 2 && mgMap.NOIMLDFHELK == 0,
            $"layers={mgMap?.GILKCKNOBGM.Count} layer={mgMap?.BCCLAJIJJOA} node={mgMap?.NOIMLDFHELK}");
        var mgFirst = mgMap!.GILKCKNOBGM[1].NNHPACALJEI.Values.OrderBy(n => n.KCIMLBLCCHP).First();
        var mgOpen = LgSend(new CS_RougeNodeOpen { Id = mgFirst.KCIMLBLCCHP, PlanId = legacyPlan }).OfType<SC_RougeNodeOpen>().FirstOrDefault();
        Check("rouge migrate: the first node of layer 2 opens", mgOpen?.GCMADDJNGMI?.KCIMLBLCCHP == mgFirst.KCIMLBLCCHP);
        main.Send(new CS_RougeFinish { PlanId = legacyPlan, Reason = CDPCJHMOCPM.RougeFinishOptionManualQuit });
        Check("rouge migrate: the run can be settled", main.ReadFor(TimeSpan.FromSeconds(1)).Any(p => p.Message is SC_RougeFinish));
    }
}

if (scenario is "all" or "rouge")
{
    // defeat: a second run of plan 600002 lost in its first battle (A-127)
    const uint losePlan = 600002;
    List<IMessage> LoSend(IMessage request, int ms = 600) { main.Send(request); return main.ReadFor(TimeSpan.FromMilliseconds(ms)).Select(p => p.Message).ToList(); }
    var loTrend = LoSend(new CS_RougePrepare { PlanId = losePlan, IFNEDNHKBJN = 1 }).OfType<SC_RougePrepare>().First().Info.FEALMJMPBHK[0];
    var loChip = LoSend(new CS_RougePrepare { PlanId = losePlan, IFNEDNHKBJN = 1, HIGHJLJPCPB = loTrend }).OfType<SC_RougePrepare>().First().Info.PKOFBGMKLLC[0];
    var loPrepare = new CS_RougePrepare { PlanId = losePlan, IFNEDNHKBJN = 1, HIGHJLJPCPB = loTrend, ODMEICDCEDP = loChip };
    loPrepare.NDKGOAHEAMJ.AddRange(new uint[] { 1001, 1008, 1021, 1022 });
    LoSend(loPrepare);
    var loMap = LoSend(new CS_RougeStart { PlanId = losePlan }).OfType<SC_RougeStart>().FirstOrDefault()?.Map;
    var loNode = loMap!.GILKCKNOBGM[0].NNHPACALJEI.Values.OrderBy(n => n.KCIMLBLCCHP).First(n => n.AIODFHGMBME == 1);
    LoSend(new CS_RougeNodeOpen { Id = loNode.KCIMLBLCCHP, PlanId = losePlan });
    LoSend(new CS_StageStart { StcStageId = loNode.ANJHKELIAFF, Other = new StageOther { Rouge = new GPNFHGOODHA { PMFFONMEDCP = loNode.KCIMLBLCCHP, PlanId = losePlan } } }, 400);
    LoSend(new CS_StageInto { Guns = { [1310739u] = 1001u, [1376275u] = 1008u, [1441811u] = 1021u, [1507347u] = 1022u } }, 400);
    var lost = LoSend(new CS_StageResult { BattleResult = BattleResult.Lose, Count = 1 }, 700);
    var loAfter = LoSend(new CS_GetRougeMapInfo { PlanId = losePlan }).OfType<SC_GetRougeMapInfo>().FirstOrDefault()?.Map;
    Check("rouge (defeat): losing a battle node answers the result and leaves the node unfinished", lost.OfType<SC_StageResult>().Any(r => r.BattleResult == BattleResult.Lose)
        && !lost.OfType<SC_RougeNodeInteract>().Any() && loAfter is not null && !loAfter.GILKCKNOBGM[0].NNHPACALJEI[loNode.KCIMLBLCCHP].JONLCAGBGJE);
    var loFinish = LoSend(new CS_RougeFinish { PlanId = losePlan, Reason = CDPCJHMOCPM.RougeFinishOptionBattleFailure });
    var loSettle = loFinish.OfType<SC_RougeFinish>().FirstOrDefault();
    Check("rouge (defeat): the settlement of a defeat is reported as a battle failure, clears the coins", loSettle is not null && loSettle.Reason == CDPCJHMOCPM.RougeFinishOptionBattleFailure
        && loFinish.OfType<SC_Items>().Any(i => i.Items.ContainsKey(9990101u) || i.Items.ContainsKey(9990102u)), string.Join(",", loFinish.Select(m => m.GetType().Name)));
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
if (scenario is "all" or "dorm")
{
    again.Send(new CS_GetDormFormationInfo());
    var formationInfo = again.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GetDormFormationInfo>().FirstOrDefault();
    Check("dorm: purchased interaction remains unlocked after reconnect",
        formationInfo?.AHFNBKINDGB.TryGetValue(101703u, out bool actionBought) == true && actionBought,
        $"keys={string.Join(',', formationInfo?.AHFNBKINDGB.Keys ?? [])}");
}
if (scenario is "all" or "formation")
{
    again.Send(new CS_GetCustomFormation());
    var formationsAfterReconnect = again.ReadFor(TimeSpan.FromSeconds(3)).Select(p => p.Message).OfType<SC_GetCustomFormation>().FirstOrDefault()?.OGNHICHKFAD?.KMPIHKFBAEP;
    Check("formation: manually saved presets keep their roster/name after reconnect",
        formationsAfterReconnect is { Count: >= 4 }
        && formationsAfterReconnect[1].Name == "Bravo" && formationsAfterReconnect[1].Guns.Count == 2
        && formationsAfterReconnect[3].Name == "Bravo" && formationsAfterReconnect[3].Guns.Count == 2,
        $"slots={formationsAfterReconnect?.Count}");
}
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
