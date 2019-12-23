using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace TuPack
{
    internal class MethodProcessor
    {
        MethodDefinition method;
        MethodBody body;

        string name;
        TracerFunctions tracerFunctions;

        public MethodProcessor(MethodDefinition method, TracerFunctions funcs)
        {
            this.method = method;
            name = method.FullName;
            tracerFunctions = funcs;
        }

        public void Process()
        {
            try
            {
                InnerProcess();
            }
            catch (Exception e)
            {
                throw new Exception($"An error occurred processing '{method.FullName}'. Error: {e.Message}", e);
            }
        }

        void InnerProcess()
        {
            body = method.Body;
            body.SimplifyMacros();

            var firstInstruction = FirstInstructionSkipCtor();
            var indexOf = body.Instructions.IndexOf(firstInstruction);
            var returnInstruction = FixReturns();
            InjectTracerBegin(indexOf);
            var indexOfReturn = body.Instructions.IndexOf(returnInstruction);
            var beforeReturn = Instruction.Create(OpCodes.Nop);
            body.Instructions.Insert(indexOfReturn, beforeReturn);

            indexOfReturn = body.Instructions.IndexOf(returnInstruction);
            InjectTracerEnd(indexOfReturn);

            var handler = new ExceptionHandler(ExceptionHandlerType.Finally)
            {
                TryStart = firstInstruction,
                TryEnd = beforeReturn,
                HandlerStart = beforeReturn,
                HandlerEnd = returnInstruction
            };
            body.ExceptionHandlers.Add(handler);
            body.InitLocals = true;
            body.OptimizeMacros();
        }

        void InjectTracerBegin(int index)
        {
            body.Insert(index, new[]
            {
            Instruction.Create(OpCodes.Ldstr, name),
            Instruction.Create(OpCodes.Call, tracerFunctions.Begin)
        });
        }

        void InjectTracerEnd(int index)
        {
            body.Insert(index, new[]
            {
            Instruction.Create(OpCodes.Ldstr, name),
            Instruction.Create(OpCodes.Call, tracerFunctions.End),
            Instruction.Create(OpCodes.Endfinally)
        });
        }

        Instruction FirstInstructionSkipCtor()
        {
            if (method.IsInstanceConstructor())
            {
                foreach (var instruction in body.Instructions)
                {
                    if (instruction.OpCode != OpCodes.Call)
                    {
                        continue;
                    }
                    var methodReference = instruction.Operand as MethodReference;
                    if (methodReference.Name != ".ctor")
                    {
                        continue;
                    }
                    if (methodReference.DeclaringType != method.DeclaringType.BaseType)
                    {
                        continue;
                    }
                    return instruction.Next;
                }
            }
            return body.Instructions.First();
        }

        Instruction FixReturns()
        {
            var instructions = body.Instructions;
            if (method.ReturnType.FullName == method.Module.TypeSystem.Void.FullName)
            {
                var lastRet = Instruction.Create(OpCodes.Ret);

                foreach (var instruction in instructions)
                {
                    if (instruction.OpCode == OpCodes.Ret)
                    {
                        instruction.OpCode = OpCodes.Leave;
                        instruction.Operand = lastRet;
                    }
                }
                instructions.Add(lastRet);
                return lastRet;
            }
            var returnVariable = new VariableDefinition(method.ReturnType);
            body.Variables.Add(returnVariable);
            var lastLd = Instruction.Create(OpCodes.Ldloc, returnVariable);
            for (var index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (instruction.OpCode == OpCodes.Ret)
                {
                    instruction.OpCode = OpCodes.Stloc;
                    instruction.Operand = returnVariable;
                    index++;
                    instructions.Insert(index, Instruction.Create(OpCodes.Leave, lastLd));
                }
            }
            instructions.Add(lastLd);
            instructions.Add(Instruction.Create(OpCodes.Ret));
            return lastLd;
        }
    }
}