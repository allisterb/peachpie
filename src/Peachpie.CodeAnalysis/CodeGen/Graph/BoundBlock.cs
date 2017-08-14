﻿using Microsoft.CodeAnalysis;
using Pchp.CodeAnalysis.CodeGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using Devsense.PHP.Text;
using Pchp.CodeAnalysis.Symbols;

namespace Pchp.CodeAnalysis.Semantics.Graph
{
    partial class BoundBlock : IGenerator
    {
        internal override void Emit(CodeGenerator cg)
        {
            // emit contained statements
            if (_statements.Count != 0)
            {
                _statements.ForEach(cg.Generate);
            }

            //
            cg.Generate(this.NextEdge);
        }

        void IGenerator.Generate(CodeGenerator cg) => Emit(cg);

        /// <summary>
        /// Helper comparer defining order in which are blocks emitted if there is more than one in the queue.
        /// Can be used for optimizing branches heuristically.
        /// </summary>
        internal sealed class EmitOrderComparer : IComparer<BoundBlock>
        {
            // TODO: blocks emit priority

            public static readonly EmitOrderComparer Instance = new EmitOrderComparer();
            private EmitOrderComparer() { }
            public int Compare(BoundBlock x, BoundBlock y) => x.Ordinal - y.Ordinal;
        }
    }

    partial class StartBlock
    {
        internal override void Emit(CodeGenerator cg)
        {
            cg.Builder.DefineInitialHiddenSequencePoint();

            // first brace sequence point
            var body = cg.Routine.Syntax.BodySpanOrInvalid();
            if (body.IsValid && cg.IsDebug)
            {
                cg.EmitSequencePoint(new Span(body.Start, 1));
                cg.EmitOpCode(ILOpCode.Nop);
            }

            //
            if (cg.IsDebug)
            {
                if (cg.Routine.IsStatic)
                {
                    // Debug.Assert(<context> != null);
                    cg.EmitDebugAssertNotNull(cg.ContextPlaceOpt, "Context cannot be null.");
                }

                // TODO: emit parameters checks
            }

            //
            var locals = cg.Routine.LocalsTable;

            // in case of script, declare the script, functions and types
            if (cg.Routine is SourceGlobalMethodSymbol)
            {
                // <ctx>.OnInclude<TScript>()
                cg.EmitLoadContext();
                cg.EmitCall(ILOpCode.Callvirt, cg.CoreMethods.Context.OnInclude_TScript.Symbol.Construct(cg.Routine.ContainingType));

                // <ctx>.DeclareFunction()
                cg.Routine.ContainingFile.Functions
                    .Where(f => !f.IsConditional)
                    .ForEach(cg.EmitDeclareFunction);
                // <ctx>.DeclareType()
                cg.Routine.ContainingFile.ContainedTypes
                    .Where(t => !t.IsConditional && !t.IsAnonymousType)   // non conditional declaration within this file
                    .ForEach(cg.EmitDeclareType);
            }
            else
            {
                //If it has unoptimized locals and they're not initilized externally -> need to initialize them
                if (cg.HasUnoptimizedLocals && !cg.InitializedLocals)
                {
                    // <locals> = new PhpArray(HINTCOUNT)
                    cg.LocalsPlaceOpt.EmitStorePrepare(cg.Builder);
                    cg.Builder.EmitIntConstant(locals.Count);    // HINTCOUNT
                    cg.EmitCall(ILOpCode.Newobj, cg.CoreMethods.Ctors.PhpArray_int);
                    cg.LocalsPlaceOpt.EmitStore(cg.Builder);
                }
            }

            if (!cg.InitializedLocals)
            {
                // variables/parameters initialization
                foreach (var loc in locals.Variables)
                {
                    loc.EmitInit(cg);
                }
            }

            // if generator method: emit switch table for continuation & change state to -1 (running)
            if (cg.Routine.IsGeneratorMethod())
            {
                EmitStateMachineMethodStart(cg);
            }

            //
            base.Emit(cg);
        }

        private static void EmitStateMachineMethodStart(CodeGenerator cg)
        {
            // local <state> = g._state that is switched on (can't switch on remote field)
            cg.EmitGeneratorInstance();
            cg.EmitCall(ILOpCode.Call, cg.CoreMethods.Operators.GetGeneratorState_Generator);

            var stateTmpLocal = cg.GetTemporaryLocal(cg.CoreTypes.Int32);
            cg.Builder.EmitLocalStore(stateTmpLocal);


            // g._state = -1 : running
            cg.EmitGeneratorInstance();
            cg.Builder.EmitIntConstant(-1);
            cg.EmitCall(ILOpCode.Call, cg.CoreMethods.Operators.SetGeneratorState_Generator_int);


            // create label for situation when state doesn't correspond to continuation: 0 -> didn't run to first yield
            var noContinuationLabel = new NamedLabel("noStateContinuation");

            // prepare jump table from yields
            var yields = cg.Routine.ControlFlowGraph.Yields;
            var yieldExLabels = new List<KeyValuePair<ConstantValue, object>>();
            for (var i = 0; i < yields.Length; i++)
            {
                // i+1 because labels have 1-based index (zero is reserved for run to first yield)
                yieldExLabels.Add(new KeyValuePair<ConstantValue, object>(ConstantValue.Create(i + 1), yields[i]));
            }

            // emit switch table that based on g._state jumps to appropriate continuation label
            cg.Builder.EmitIntegerSwitchJumpTable(yieldExLabels.ToArray(), noContinuationLabel, stateTmpLocal, Microsoft.Cci.PrimitiveTypeCode.Int32);
            cg.ReturnTemporaryLocal(stateTmpLocal);


            cg.Builder.MarkLabel(noContinuationLabel);
        }
    }

    partial class ExitBlock
    {
        /// <summary>
        /// Temporary local variable for return.
        /// </summary>
        private Microsoft.CodeAnalysis.CodeGen.LocalDefinition _rettmp;

        /// <summary>
        /// Return label.
        /// </summary>
        private object _retlbl;

        /// <summary>
        /// Stores value from top of the evaluation stack to a temporary variable which will be returned from the exit block.
        /// </summary>
        internal void EmitTmpRet(CodeGenerator cg, Symbols.TypeSymbol stack)
        {
            // lazy initialize
            if (_retlbl == null)
            {
                _retlbl = new NamedLabel("<return>");
            }

            if (_rettmp == null)
            {
                var rtype = cg.Routine.ReturnType;
                if (rtype.SpecialType != SpecialType.System_Void)
                {
                    _rettmp = cg.GetTemporaryLocal(rtype);
                }
            }

            // <rettmp> = <stack>;
            if (_rettmp != null)
            {
                cg.EmitConvert(stack, 0, (Symbols.TypeSymbol)_rettmp.Type);
                cg.Builder.EmitLocalStore(_rettmp);
                cg.Builder.EmitBranch(ILOpCode.Br, _retlbl);
            }
            else
            {
                cg.EmitPop(stack);
            }
        }

        internal override void Emit(CodeGenerator cg)
        {
            // if generator method: set state to -2 (closed)
            if (cg.Routine.IsGeneratorMethod())
            {
                // g._state = -2 (closed): got to the end of the generator method
                cg.EmitGeneratorInstance();
                cg.Builder.EmitIntConstant(-2);
                cg.EmitCall(ILOpCode.Call, cg.CoreMethods.Operators.SetGeneratorState_Generator_int);

                cg.Builder.EmitRet(true);
                return;
            }

            // note: ILBuider removes eventual unreachable .ret opcode

            if (_retlbl != null && _rettmp == null)
            {
                cg.Builder.MarkLabel(_retlbl);
            }

            // return <default>;
            cg.EmitRetDefault();
            cg.Builder.AssertStackEmpty();

            // return <rettemp>;
            if (_rettmp != null)
            {
                Debug.Assert(_retlbl != null);
                cg.Builder.MarkLabel(_retlbl);

                // note: _rettmp is always initialized since we branch to _retlbl only after storing to _rettmp

                cg.Builder.EmitLocalLoad(_rettmp);
                cg.Builder.EmitRet(false);
                cg.Builder.AssertStackEmpty();
            }
        }
    }
}
