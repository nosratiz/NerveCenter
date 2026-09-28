using FluentAssertions;
using StackExchange.Redis;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisAclPermissionEvaluatorTests
{
    private static readonly string[] FlushDbCategories = ["keyspace", "write", "slow", "dangerous"];
    private static readonly string[] PublishCategories = ["pubsub", "fast"];

    [Fact]
    public void Null_or_empty_commands_field_fails_open_to_allowed()
    {
        RedisAclPermissionEvaluator.IsAllowed(null, "FLUSHDB", FlushDbCategories).Should().BeTrue();
        RedisAclPermissionEvaluator.IsAllowed("", "FLUSHDB", FlushDbCategories).Should().BeTrue();
        RedisAclPermissionEvaluator.IsAllowed("   ", "FLUSHDB", FlushDbCategories).Should().BeTrue();
    }

    [Fact]
    public void Plus_at_all_allows_every_command()
    {
        RedisAclPermissionEvaluator.IsAllowed("+@all", "FLUSHDB", FlushDbCategories).Should().BeTrue();
        RedisAclPermissionEvaluator.IsAllowed("+@all", "PUBLISH", PublishCategories).Should().BeTrue();
    }

    [Fact]
    public void Minus_at_all_denies_every_command()
    {
        RedisAclPermissionEvaluator.IsAllowed("-@all", "FLUSHDB", FlushDbCategories).Should().BeFalse();
        RedisAclPermissionEvaluator.IsAllowed("-@all", "PUBLISH", PublishCategories).Should().BeFalse();
    }

    [Fact]
    public void Minus_at_all_plus_a_known_category_allows_commands_in_that_category()
    {
        RedisAclPermissionEvaluator.IsAllowed("-@all +@dangerous", "FLUSHDB", FlushDbCategories).Should().BeTrue();
        RedisAclPermissionEvaluator.IsAllowed("-@all +@pubsub", "PUBLISH", PublishCategories).Should().BeTrue();
    }

    [Fact]
    public void An_unrelated_category_grant_does_not_allow_the_command()
    {
        RedisAclPermissionEvaluator.IsAllowed("-@all +@read", "FLUSHDB", FlushDbCategories).Should().BeFalse();
    }

    [Fact]
    public void A_specific_command_denial_overrides_an_earlier_category_grant()
    {
        RedisAclPermissionEvaluator.IsAllowed("+@all -flushdb", "FLUSHDB", FlushDbCategories).Should().BeFalse();
    }

    [Fact]
    public void A_specific_command_grant_overrides_an_earlier_all_denial()
    {
        RedisAclPermissionEvaluator.IsAllowed("-@all +flushdb", "FLUSHDB", FlushDbCategories).Should().BeTrue();
    }

    [Fact]
    public void Last_matching_token_wins_when_several_conflict()
    {
        RedisAclPermissionEvaluator.IsAllowed("-@all +@dangerous -flushdb", "FLUSHDB", FlushDbCategories).Should().BeFalse();
        RedisAclPermissionEvaluator.IsAllowed("-@all -flushdb +@dangerous", "FLUSHDB", FlushDbCategories).Should().BeTrue();
    }

    [Fact]
    public void Command_and_category_matching_is_case_insensitive()
    {
        RedisAclPermissionEvaluator.IsAllowed("-@all +FlushDB", "flushdb", FlushDbCategories).Should().BeTrue();
        RedisAclPermissionEvaluator.IsAllowed("-@all +@DANGEROUS", "FLUSHDB", FlushDbCategories).Should().BeTrue();
    }

    [Fact]
    public void A_pipe_subcommand_suffix_is_stripped_before_matching()
    {
        // "+acl|whoami" grants only the ACL WHOAMI subcommand -- must not be mistaken for a grant
        // of the bare "acl" command or bleed into an unrelated command's evaluation.
        RedisAclPermissionEvaluator.IsAllowed("-@all +acl|whoami", "FLUSHDB", FlushDbCategories).Should().BeFalse();
    }

    [Fact]
    public void Allcommands_and_nocommands_legacy_tokens_are_recognized()
    {
        RedisAclPermissionEvaluator.IsAllowed("nocommands +flushdb", "FLUSHDB", FlushDbCategories).Should().BeTrue();
        RedisAclPermissionEvaluator.IsAllowed("allcommands -flushdb", "FLUSHDB", FlushDbCategories).Should().BeFalse();
    }

    [Fact]
    public void A_default_users_unrestricted_grant_allows_publish_and_flushdb()
    {
        // The out-of-the-box "default" Redis user's ACL GETUSER commands field.
        RedisAclPermissionEvaluator.IsAllowed("+@all", "PUBLISH", PublishCategories).Should().BeTrue();
    }

    // ---- ExtractCommandsField ----

    private static RedisResult Scalar(RedisValue v) => RedisResult.Create(v);
    private static RedisResult Arr(params RedisResult[] items) => RedisResult.Create(items);

    [Fact]
    public void ExtractCommandsField_finds_the_commands_entry_among_other_fields()
    {
        var reply = Arr(
            Scalar("flags"), Arr(Scalar("on"), Scalar("allkeys")),
            Scalar("passwords"), Arr(),
            Scalar("commands"), Scalar("-@all +get +set"),
            Scalar("keys"), Scalar("~*"),
            Scalar("channels"), Scalar(""),
            Scalar("selectors"), Arr());

        RedisAclPermissionEvaluator.ExtractCommandsField(reply).Should().Be("-@all +get +set");
    }

    [Fact]
    public void ExtractCommandsField_returns_null_when_the_reply_is_nil()
    {
        // ACL GETUSER for a nonexistent username returns a nil reply.
        RedisAclPermissionEvaluator.ExtractCommandsField(RedisResult.Create(RedisValue.Null)).Should().BeNull();
    }

    [Fact]
    public void ExtractCommandsField_returns_null_when_no_commands_field_is_present()
    {
        var reply = Arr(Scalar("flags"), Arr(Scalar("on")));

        RedisAclPermissionEvaluator.ExtractCommandsField(reply).Should().BeNull();
    }
}
