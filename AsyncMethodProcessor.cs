using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;

namespace TuPack
{
    internal class AsyncMethodProcessor
    {
        static Int32 Count;
        MethodDefinition method;
        TypeDefinition stateMachineType;
        MethodBody body;
        List<Instruction> returnPoints;

        Int32 asyncIndex;
        string name;

        TracerFunctions tracerFunctions;

        public AsyncMethodProcessor(MethodDefinition method, TracerFunctions funcs)
        {
            this.method = method;
            asyncIndex = Count++;

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
            var asyncAttribute = method.GetAsyncStateMachineAttribute();
            stateMachineType = asyncAttribute.ConstructorArguments.Select(ctor => (TypeDefinition)ctor.Value).Single();
            var moveNextMethod = stateMachineType.Methods.Single(m => m.Name == "MoveNext");
            body = moveNextMethod.Body;

            body.SimplifyMacros();

            returnPoints = GetAsyncReturns(body.Instructions).ToList();

            // First, fall back to old mechanism
            int index;

            // Check roslyn usage
            var firstStateUsage = (
                from instruction in body.Instructions
                let fieldReference = instruction.Operand as FieldReference
                where instruction.OpCode == OpCodes.Ldfld && fieldReference != null && fieldReference.Name.Contains("__state")
                select instruction
                ).FirstOrDefault();

            if (firstStateUsage is null)
            {
                // Probably compiled without roslyn, inject at first line
                index = 0;
            }
            else
            {
                // Initial code looks like this (hence the -1):
                //
                // <== this is where we want to start the stopwatch
                // ldarg.0
                // ldfld __state
                // stloc.0
                // ldloc.0
                index = body.Instructions.IndexOf(firstStateUsage) - 1;
            }

            InjectTracer(index);

            foreach (var returnPoint in returnPoints)
            {
                FixReturn(returnPoint);
            }

            body.InitLocals = true;
            body.OptimizeMacros();
        }

        void FixReturn(Instruction returnPoint)
        {
            var opCode = returnPoint.OpCode;
            var operand = returnPoint.Operand as Instruction;

            returnPoint.OpCode = OpCodes.Nop;
            returnPoint.Operand = null;

            var instructions = body.Instructions;
            var indexOf = instructions.IndexOf(returnPoint);

            instructions.Insert(++indexOf, Instruction.Create(OpCodes.Ldc_I4, asyncIndex));
            instructions.Insert(++indexOf, Instruction.Create(OpCodes.Ldstr, name));
            instructions.Insert(++indexOf, Instruction.Create(OpCodes.Call, tracerFunctions.End));

            indexOf++;

            if (opCode == OpCodes.Leave || opCode == OpCodes.Leave_S)
            {
                instructions.Insert(indexOf, Instruction.Create(opCode, operand));
            }
            else
            {
                instructions.Insert(indexOf, Instruction.Create(opCode));
            }
        }

        void InjectTracer(int index)
        {
            body.Insert(index, new[]
            {
                Instruction.Create(OpCodes.Ldc_I4, asyncIndex),
                Instruction.Create(OpCodes.Ldstr, name),
                Instruction.Create(OpCodes.Call, tracerFunctions.Begin)
            });
        }

        static IEnumerable<Instruction> GetAsyncReturns(Collection<Instruction> instructions)
        {
            // There are 3 possible return points:
            //
            // 1) async code:
            //      awaiter.GetResult();
            //      awaiter = new TaskAwaiter();
            //
            // 2) exception handling
            //      L_00d5: ldloc.1
            //      L_00d6: call instance void [mscorlib]System.Runtime.CompilerServices.AsyncTaskMethodBuilder::SetException(class [mscorlib]System.Exception)
            //
            // 3) all other returns
            //
            // We can do this smart by searching for all leave and leave_S op codes and check if they point to the last
            // instruction of the method. This equals a "return" call.

            var returnStatements = new List<Instruction>();

            var possibleReturnStatements = new List<Instruction>();

            // Look for the last leave statement (that is the line all "return" statements go to)
            for (var i = instructions.Count - 1; i >= 0; i--)
            {
                var instruction = instructions[i];
                if (instruction.IsLeaveInstruction())
                {
                    possibleReturnStatements.Add(instructions[i + 1]);
                    break;
                }
            }

            for (var i = 0; i < instructions.Count; i++)
            {
                var instruction = instructions[i];
                if (instruction.IsLeaveInstruction())
                {
                    if (possibleReturnStatements.Any(x => ReferenceEquals(instruction.Operand, x)))
                    {
                        // This is a return statement, this covers scenarios 1 and 3
                        returnStatements.Add(instruction);
                    }
                    else
                    {
                        // Check if we set an exception in this block, this covers scenario 2
                        for (var j = i - 3; j < i; j++)
                        {
                            var previousInstruction = instructions[j];
                            if (previousInstruction.OpCode == OpCodes.Call)
                            {
                                if (previousInstruction.Operand is MethodReference methodReference)
                                {
                                    if (methodReference.Name.Equals("SetException"))
                                    {
                                        returnStatements.Add(instruction);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return returnStatements;
        }
    }
}