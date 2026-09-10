using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimBoosted;

internal static class SendZdoTransform
{
    internal static IEnumerable<CodeInstruction> Apply(IEnumerable<CodeInstruction> instructions, MethodInfo window, MethodInfo dispatch)
    {
        var result = new List<CodeInstruction>(); int windows = 0, sends = 0;
        var invoke = AccessTools.Method(typeof(ZRpc), "Invoke", new[] { typeof(string), typeof(object[]) });
        foreach (var original in instructions)
        {
            var code = new CodeInstruction(original);
            if (code.opcode == OpCodes.Ldc_I4 && (int)code.operand == SendWindowPolicy.VanillaBytes)
            {
                windows++; code.opcode = OpCodes.Ldarg_1; code.operand = null; result.Add(code);
                result.Add(new CodeInstruction(OpCodes.Ldarg_2));
                result.Add(new CodeInstruction(OpCodes.Call, window));
            }
            else
            {
                if (code.Calls(invoke)) { sends++; code.opcode = OpCodes.Call; code.operand = dispatch; }
                result.Add(code);
            }
        }
        if (windows != 2 || sends != 1) throw new InvalidOperationException("SendZDOs transform requires exactly two reviewed window constants and one RPC dispatch");
        return result;
    }
}
