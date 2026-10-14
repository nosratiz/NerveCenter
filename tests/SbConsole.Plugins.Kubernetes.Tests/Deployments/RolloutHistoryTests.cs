using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Deployments;

namespace SbConsole.Plugins.Kubernetes.Tests.Deployments;

public class RolloutHistoryTests
{
    private static readonly DateTimeOffset T0 = TestData.T0;

    private static K8sDeploymentDetail Healthy(K8sDeploymentDetail d) => d with
    {
        Deployment = d.Deployment with
        {
            Conditions = [new K8sCondition("Progressing", "True", "NewReplicaSetAvailable", null, T0.AddMinutes(-5))],
        },
    };

    [Fact]
    public void Build_lists_revisions_newest_first_with_current_stuck_and_rollback_flags()
    {
        var rows = RolloutHistory.Build(TestData.PaymentGatewayDetail());

        rows.Select(r => r.Revision).Should().Equal(23, 22, 21, 20, 19);
        var current = rows[0];
        current.IsCurrent.Should().BeTrue();
        current.IsStuck.Should().BeTrue();
        current.CanRollback.Should().BeFalse();
        current.Image.Should().Be("ghcr.io/nervecenter/payment-gateway:2.14.1");
        current.ChangeCause.Should().Be("bump sdk");
        current.CreatedAt.Should().Be(T0.AddMinutes(-38));
        current.ReadyPods.Should().Be(0);
        rows.Skip(1).Should().OnlyContain(r => !r.IsCurrent && !r.IsStuck && r.CanRollback);
        rows[1].ReadyPods.Should().Be(2);
        rows[3].ChangeCause.Should().Be("manual");
    }

    [Fact]
    public void Build_orders_by_revision_not_by_input_order_and_only_templates_can_be_rolled_back_to()
    {
        var d = TestData.PaymentGatewayDetail();
        d = d with
        {
            ReplicaSets = [d.ReplicaSets[3] with { HasTemplate = false }, d.ReplicaSets[1], d.ReplicaSets[0]],
        };

        var rows = RolloutHistory.Build(d);

        rows.Select(r => r.Revision).Should().Equal(23, 22, 20);
        rows.Single(r => r.Revision == 20).CanRollback.Should().BeFalse();
    }

    [Fact]
    public void Healthy_current_revision_is_current_but_not_stuck()
    {
        var rows = RolloutHistory.Build(Healthy(TestData.PaymentGatewayDetail()));

        rows[0].IsCurrent.Should().BeTrue();
        rows[0].IsStuck.Should().BeFalse();
    }

    [Fact]
    public void Stuck_describes_a_progress_deadline_exceeded_rollout()
    {
        var stuck = RolloutHistory.Stuck(TestData.PaymentGatewayDetail(), T0);

        stuck.Should().Be(new StuckRollout(23, TimeSpan.FromMinutes(38), 2, "OOM", 22, 2));
    }

    [Fact]
    public void Stuck_has_no_reason_when_the_crash_loops_are_not_oom()
    {
        var d = TestData.PaymentGatewayDetail();
        d = d with { Pods = d.Pods.Select(p => p with { LastTerminationReason = p.LastTerminationReason is null ? null : "Error" }).ToList() };

        var stuck = RolloutHistory.Stuck(d, T0);

        stuck!.Reason.Should().BeNull();
        stuck.CrashLoopingPods.Should().Be(2);
    }

    [Fact]
    public void Stuck_ignores_crash_loops_in_old_replica_sets()
    {
        var d = TestData.PaymentGatewayDetail();
        d = d with
        {
            Pods = d.Pods.Select(p => p.ReplicaSet == "payment-gateway-7d9f8" ? p with { Status = "Pending", LastTerminationReason = null }
                : p with { Status = "CrashLoopBackOff", LastTerminationReason = "OOMKilled" }).ToList(),
        };

        var stuck = RolloutHistory.Stuck(d, T0);

        stuck!.CrashLoopingPods.Should().Be(0);
        stuck.Reason.Should().BeNull();
    }

    [Theory]
    [InlineData("False", "ReplicaSetUpdated")]
    [InlineData("True", "ProgressDeadlineExceeded")]
    [InlineData("True", "NewReplicaSetAvailable")]
    public void Stuck_is_null_unless_progressing_is_false_with_progress_deadline_exceeded(string status, string reason)
    {
        var d = TestData.PaymentGatewayDetail();
        d = d with
        {
            Deployment = d.Deployment with { Conditions = [new K8sCondition("Progressing", status, reason, null, T0.AddMinutes(-1))] },
        };

        RolloutHistory.Stuck(d, T0).Should().BeNull();
    }

    [Fact]
    public void Stuck_without_any_serving_old_revision_has_no_serving_revision()
    {
        var d = TestData.PaymentGatewayDetail();
        d = d with { ReplicaSets = d.ReplicaSets.Select(r => r.IsCurrent ? r : r with { Ready = 0, Desired = 0 }).ToList() };

        var stuck = RolloutHistory.Stuck(d, T0);

        stuck!.ServingRevision.Should().BeNull();
        stuck.ServingPods.Should().Be(0);
    }

    [Fact]
    public void Rollback_target_is_the_serving_revision()
    {
        RolloutHistory.RollbackTarget(TestData.PaymentGatewayDetail()).Should().Be(22);
    }

    [Fact]
    public void Rollback_target_falls_back_to_the_previous_revision_with_a_template()
    {
        var d = TestData.PaymentGatewayDetail();
        d = d with
        {
            ReplicaSets = d.ReplicaSets.Select(r => r.IsCurrent ? r
                : r.Revision == 22 ? r with { Ready = 0, Desired = 0, HasTemplate = false }
                : r).ToList(),
        };

        RolloutHistory.RollbackTarget(d).Should().Be(21);
    }

    [Fact]
    public void Rollback_target_is_null_with_a_single_revision()
    {
        var d = TestData.PaymentGatewayDetail();
        d = d with { ReplicaSets = [d.ReplicaSets[0]] };

        RolloutHistory.RollbackTarget(d).Should().BeNull();
    }
}
