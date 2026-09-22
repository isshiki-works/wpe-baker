local STEAMID = "76561198000000000"

return {
    steamid = STEAMID,
    qr_begin = {
        response = {
            client_id = "123456789",
            request_id = "cmVxdWVzdA==",
            challenge_url = "https://s.team/q/example",
            interval = 1.5,
        },
    },
    qr_pending = { response = { had_remote_interaction = false } },
    qr_confirmation = { response = { had_remote_interaction = true } },
    qr_rotation = {
        response = {
            new_client_id = "987654321",
            new_challenge_url = "https://s.team/q/rotated",
            had_remote_interaction = false,
        },
    },
    qr_success = {
        response = {
            account_name = "fixture-account",
            access_token = "header.qr_access.signature",
            refresh_token = "header.refresh.signature",
        },
    },
    web_finalize = {
        steamID = STEAMID,
        transfer_info = {
            {
                url = "https://steamcommunity.com/login/settoken",
                params = { nonce = "community-transfer" },
            },
            {
                url = "https://store.steampowered.com/login/settoken",
                params = { nonce = "store-transfer" },
            },
            {
                url = "https://help.steampowered.com/login/settoken",
                params = { nonce = "help-transfer" },
            },
            {
                url = "https://checkout.steampowered.com/login/settoken",
                params = { nonce = "checkout-transfer" },
            },
            {
                url = "https://steam.tv/login/settoken",
                params = { nonce = "tv-transfer" },
            },
        },
    },
    community_cookie = "steamLoginSecure=" .. STEAMID
        .. "%7C%7Cheader.community.signature; Domain=steamcommunity.com; Path=/; Secure",
    short_community_cookie = "steamLoginSecure=" .. STEAMID
        .. "%7C%7Cheader.short_community.signature; Domain=steamcommunity.com; Path=/; Secure",
    store_cookie = "steamLoginSecure=" .. STEAMID
        .. "%7C%7Cheader.store.signature; Domain=store.steampowered.com; Path=/; Secure",
    help_cookie = "steamLoginSecure=" .. STEAMID
        .. "%7C%7Cheader.store.signature; Domain=help.steampowered.com; Path=/; Secure",
    checkout_cookie = "steamLoginSecure=" .. STEAMID
        .. "%7C%7Cheader.store.signature; Domain=checkout.steampowered.com; Path=/; Secure",
    tv_cookie = "steamLoginSecure=" .. STEAMID
        .. "%7C%7Cheader.store.signature; Domain=steam.tv; Path=/; Secure",
    profile_page = '<a class="user_avatar"><img src="https://avatars.steamstatic.com/avatar.jpg"></a>',
    subscribed_page = '<div id="account_pulldown">fixture-account</div>'
        .. '<div id="SubscribeItemOptionSubscribed" '
        .. 'class="subscribeOption subscribed selected">Subscribed</div>',
    unsubscribed_page = '<div id="account_pulldown">fixture-account</div>'
        .. '<div id="SubscribeItemOptionAdd" '
        .. 'class="subscribeOption add selected">Subscribe</div>',
    unknown_page = '<div id="account_pulldown">fixture-account</div>',
    conflict_page = '<div id="account_pulldown">fixture-account</div>'
        .. '<div id="SubscribeItemOptionSubscribed" '
        .. 'class="subscribeOption subscribed selected">Subscribed</div>'
        .. '<div id="SubscribeItemOptionAdd" '
        .. 'class="subscribeOption add selected">Subscribe</div>',
    signed_out_page = '<a href="https://steamcommunity.com/login">Sign In</a>',
    -- Shape of steamcommunity.com/profiles/<id>/?xml=1: the persona name is
    -- wrapped in CDATA, and a group further down repeats tags the reader must
    -- not confuse with the profile's own.
    profile_xml = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><profile>'
        .. "<steamID64>76561198000000001</steamID64>"
        .. "<steamID><![CDATA[Fixture &amp; Author]]></steamID>"
        .. "<privacyState>public</privacyState>"
        .. "<groups><group><groupName><![CDATA[Fixture group]]></groupName></group></groups>"
        .. "</profile>",
    profile_missing_xml = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        .. "<response><error><![CDATA[The specified profile could not be found.]]></error>"
        .. "</response>",
    query_files = {
        response = {
            total = 1,
            publishedfiledetails = {
                {
                    publishedfileid = "3765064055",
                    creator = "76561198000000001",
                    title = "Fixture wallpaper",
                    preview_url = "https://example.invalid/preview.jpg",
                    short_description = "Fixture description",
                    file_size = "4096",
                    subscriptions = 42,
                    can_subscribe = true,
                    tags = {
                        { tag = "Scene" },
                        { tag = "Everyone" },
                    },
                },
            },
        },
    },
    file_details = {
        response = {
            publishedfiledetails = {
                {
                    publishedfileid = "fallback",
                    title = "Fallback wallpaper",
                    description = "Fallback description",
                    file_size = "8192",
                    tags = { { tag = "Video" } },
                },
            },
        },
    },
    mutation_accepted = { response = {} },
    -- Shape Steam writes to steamapps/workshop/appworkshop_431960.acf: one
    -- subscribed item, one subscribed item the client recorded without a size,
    -- plus a comment and an escaped quote the reader has to survive. The
    -- hand-placed directory 2589297069 appears in no record.
    workshop_acf = table.concat({
        '"AppWorkshop"',
        "{",
        '\t"appid"\t\t"431960"',
        '\t// Steam writes this file itself.',
        '\t"WorkshopItemsInstalled"',
        "\t{",
        '\t\t"3765064055"',
        "\t\t{",
        '\t\t\t"size"\t\t"4096"',
        '\t\t\t"timeupdated"\t\t"1785290565"',
        '\t\t\t"manifest"\t\t"1948827864188560204"',
        "\t\t}",
        '\t\t"3765064056"',
        "\t\t{",
        '\t\t\t"timeupdated"\t\t"1785290566"',
        '\t\t\t"manifest"\t\t"1948827864188560205"',
        "\t\t}",
        "\t}",
        '\t"WorkshopItemDetails"',
        "\t{",
        '\t\t"3765064055"',
        "\t\t{",
        '\t\t\t"manifest"\t\t"1948827864188560204"',
        '\t\t\t"title"\t\t"a \\"quoted\\" title"',
        '\t\t\t"subscribedby"\t\t"89291590"',
        "\t\t}",
        '\t\t"3765064056"',
        "\t\t{",
        '\t\t\t"manifest"\t\t"1948827864188560205"',
        '\t\t\t"subscribedby"\t\t"89291590"',
        "\t\t}",
        "\t}",
        "}",
    }, "\n"),
    workshop_acf_truncated = '"AppWorkshop"\n{\n\t"WorkshopItemDetails"\n\t{\n',
}
