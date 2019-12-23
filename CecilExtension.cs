using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TuPack
{
    static class CecilExtension
    {
        public static void Insert(this MethodBody body, int index, IEnumerable<Instruction> instructions)
        {
            instructions = instructions.Reverse();
            foreach (var instruction in instructions)
            {
                body.Instructions.Insert(index, instruction);
            }
        }
        public static bool IsLeaveInstruction(this Instruction instruction)
        {
            return instruction.OpCode == OpCodes.Leave || instruction.OpCode == OpCodes.Leave_S;
        }
        public static bool IsInstanceConstructor(this MethodDefinition methodDefinition)
        {
            return methodDefinition.IsConstructor && !methodDefinition.IsStatic;
        }
        public static CustomAttribute GetAsyncStateMachineAttribute(this MethodDefinition method)
        {
            return method.CustomAttributes.FirstOrDefault(_ => _.AttributeType.Name == "AsyncStateMachineAttribute");
        }

        public static bool IsAsync(this MethodDefinition method)
        {
            return GetAsyncStateMachineAttribute(method) != null;
        }

        public static bool IsYield(this MethodDefinition method)
        {
            if (method.ReturnType is null)
            {
                return false;
            }
            if (!method.ReturnType.Name.StartsWith("IEnumerable"))
            {
                return false;
            }
            var stateMachinePrefix = $"<{method.Name}>";
            var nestedTypes = method.DeclaringType.NestedTypes;
            return nestedTypes.Any(x => x.Name.StartsWith(stateMachinePrefix));
        }

        public static IEnumerable<MethodDefinition> ConcreteMethods(this TypeDefinition type)
        {
            return type.Methods.Where(x => !x.IsAbstract && x.HasBody && !IsEmptyConstructor(x));
        }

        static bool IsEmptyConstructor(this MethodDefinition method)
        {
            return method.Name == ".ctor" && method.Body.Instructions.Count(x => x.OpCode != OpCodes.Nop) == 3;
        }


    }
}
