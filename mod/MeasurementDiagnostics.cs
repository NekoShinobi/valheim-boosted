using System;
using System.Collections.Generic;
using System.Text;

namespace ValheimBoosted;

internal sealed class MeasurementDiagnostics
{
    private readonly Action<string> warning;
    private double nextWarning;

    internal MeasurementDiagnostics(Action<string> warning) { this.warning = warning; }

    internal MeasurementError Capture(Exception error, string operation, string context, double now)
    {
        var chain = new List<string>();
        var cause = error;
        for (int depth = 0; depth < 16; depth++)
        {
            chain.Add(cause.GetType().Name);
            if (cause.InnerException == null || depth == 15) break;
            cause = cause.InnerException;
        }
        var result = new MeasurementError
        {
            exceptionType = cause.GetType().Name,
            message = SingleLine(cause.Message, 512),
            operation = SingleLine(operation, 256),
            exceptionChain = SingleLine(string.Join(" -> ", chain), 512),
        };
        Warn(context + "; operation=" + result.operation + "\n" + error, now);
        return result;
    }

    internal void Warn(string message, double now)
    {
        // One shared limit across peers, including changing errors and native result codes.
        if (now < nextWarning) return;
        nextWarning = now + 30;
        warning(message.Length > 16384 ? message.Substring(0, 16384) : message);
    }

    private static string SingleLine(string text, int limit)
    {
        if (text == null) return "";
        var result = new StringBuilder(Math.Min(text.Length, limit));
        foreach (char c in text)
        {
            if (result.Length == limit) break;
            result.Append(char.IsControl(c) ? ' ' : c);
        }
        return result.ToString();
    }

    internal static void UpdateTransport(FeatureStatus feature, int attempts, int failures)
    {
        if (!feature.Collect) return;
        feature.invocations += attempts;
        feature.status = failures > 0 ? "degraded" : attempts > 0 ? "active" : "available";
        feature.detail = failures > 0 ? $"{failures}/{attempts} peer queries failed in this snapshot; retrying. See connection diagnostics."
            : attempts == 0 ? "Contract verified; awaiting a connected Steam peer" : null;
    }
}
