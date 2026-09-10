using System;
using System.Collections.Generic;
using System.Reflection;
using ValheimBoosted;

internal static class MeasurementChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var logs = new List<string>();
        var diagnostics = new MeasurementDiagnostics(logs.Add);
        var error = new TargetInvocationException(new TypeInitializationException("NativeBinding", new DllNotFoundException("missing native library\nsecond line")));
        var report = diagnostics.Capture(error, "Steamworks.Query", "peer session=123", 0);
        check(report.exceptionType == "DllNotFoundException" && report.message == "missing native library second line", "Underlying native error is unwrapped and kept on one line");
        check(report.exceptionChain == "TargetInvocationException -> TypeInitializationException -> DllNotFoundException", "Exception wrapper chain is preserved");
        check(logs.Count == 1 && logs[0].Contains("peer session=123") && logs[0].Contains("DllNotFoundException"), "First exception is logged with query and peer context");
        diagnostics.Capture(new InvalidOperationException("different peer error"), "Query", "peer session=456", 1);
        diagnostics.Warn("native result failure", 29.99);
        check(logs.Count == 1, "Repeated and changing errors across peers share the warning limit");
        diagnostics.Capture(error, "Query", "peer session=123", 30);
        check(logs.Count == 2, "A persistent error is logged again after 30 seconds");
        var bounded = diagnostics.Capture(new Exception(new string('x', 1000)), new string('y', 1000), "context", 31);
        check(bounded.message.Length == 512 && bounded.operation.Length == 256, "Exported diagnostics are bounded");
        var feature = new FeatureStatus { enabled = true, status = "available" };
        MeasurementDiagnostics.UpdateTransport(feature, 0, 0);
        check(feature.status == "available" && feature.detail.Contains("awaiting"), "No peers does not claim successful query execution");
        MeasurementDiagnostics.UpdateTransport(feature, 2, 1);
        check(feature.status == "degraded" && feature.Collect && feature.invocations == 2, "Partial query failure degrades reporting but permits retries");
        MeasurementDiagnostics.UpdateTransport(feature, 2, 0);
        check(feature.status == "active" && feature.detail == null && feature.invocations == 4, "Successful queries clear degraded state");
        feature.status = "configured_disabled";
        MeasurementDiagnostics.UpdateTransport(feature, 2, 0);
        check(!feature.Collect && feature.status == "configured_disabled" && feature.invocations == 4, "Runtime reporting never re-enables disabled probes");
    }
}
