﻿/*
* ==============================================================================
* Filename: InjectMethods
* Created:  2018/7/13 14:29:22
* Author:   エル・プサイ・コングリィ
* Purpose:  
* ==============================================================================
*/

#if UNITY_EDITOR
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using UnityEditor;
using UnityEngine;

namespace MikuLuaProfiler
{
    public static class InjectMethods
    {
        private static MethodBase m_getCurrentMethod;
        private static MethodDefinition m_beginSampleMethod;
        private static MethodDefinition m_endSampleMethod;
        private static MethodDefinition m_getMethodString;
        public static void InjectAllMethods()
        {
            string assemblyPath = "./Library/ScriptAssemblies/Assembly-CSharp.dll";
            bool flag = File.Exists(assemblyPath + ".mdb");
            AssemblyDefinition assembly;

            if (flag)
            {
                ReaderParameters readerParameters = new ReaderParameters
                {
                    ReadSymbols = true
                };
                assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readerParameters);
            }
            else
            {
                assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
            }
            AddResolver(assembly);

            m_getCurrentMethod = typeof(MethodBase).GetMethod("GetCurrentMethod");

            var profilerType = assembly.MainModule.GetType("MikuLuaProfiler.LuaProfiler");
            foreach (var m in profilerType.Methods)
            {
                if (m.Name == "BeginSampleCSharp")
                {
                    m_beginSampleMethod = m;
                }
                if (m.Name == "EndSampleCSharp")
                {
                    m_endSampleMethod = m;
                }
                if (m.Name == "GetMethodLineString")
                {
                    m_getMethodString = m;
                }
            }

            var module = assembly.MainModule;
            foreach (var type in assembly.MainModule.Types)
            {
                if (type.FullName.Contains("MikuLuaProfiler"))
                {
                    continue;
                }
                if (type.FullName.Contains("XLua")
                    || type.FullName.Contains("SLua")
                    || type.FullName.Contains("LuaInterface"))
                {
                    if (!type.FullName.EndsWith("Wrap"))
                        continue;
                }
                foreach (var item in type.Methods)
                {
                    if (item.IsConstructor) continue;
                    InjectTryFinally(item, module);
                }
            }

            if (flag)
            {
                WriterParameters writerParameters = new WriterParameters
                {
                    WriteSymbols = true
                };
                assembly.Write(assemblyPath, writerParameters);
            }
            else
            {
                assembly.Write(assemblyPath);
            }

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            Debug.Log("inject success");
        }

        public static void Recompile()
        {
            string path = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone);
            bool hasRecompile = false;

            string[] heads = path.Split(';');
            path = "";
            foreach (var item in heads)
            {
                if (item == "MIKU_RECOMPILE")
                {
                    hasRecompile = true;
                    continue;
                }
                path += item + ";";
            }

            if (!hasRecompile)
            {
                path += "MIKU_RECOMPILE;";
            }

            PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone, path);
        }

        private static void AddResolver(AssemblyDefinition assembly)
        {
            var assemblyResolver = assembly.MainModule.AssemblyResolver as DefaultAssemblyResolver;
            HashSet<string> paths = new HashSet<string>();
            paths.Add("./Library/ScriptAssemblies/");
            foreach (string path in (from asm in System.AppDomain.CurrentDomain.GetAssemblies()
                                     select asm.ManifestModule.FullyQualifiedName).Distinct<string>())
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!paths.Contains(dir))
                    {
                        paths.Add(dir);
                    }
                }
                catch
                {
                }
            }

            foreach (var item in paths)
            {
                assemblyResolver.AddSearchDirectory(item);
            }
        }

        private static Instruction FixReturns(MethodDefinition Method)
        {
            var body = Method.Body;
            if (Method.ReturnType.FullName == "System.Void")
            {
                var instructions = body.Instructions;
                var lastRet = Instruction.Create(OpCodes.Ret);
                instructions.Add(lastRet);

                for (var index = 0; index < instructions.Count - 1; index++)
                {
                    var instruction = instructions[index];

                    if (instruction.OpCode == OpCodes.Ret)
                    {
                        instruction.OpCode = OpCodes.Leave;
                        instruction.Operand = lastRet;
                    }
                }
                return lastRet;
            }
            else
            {
                var instructions = body.Instructions;
                var returnVariable = new VariableDefinition("methodTimerReturn", Method.ReturnType);
                body.Variables.Add(returnVariable);
                var lastLd = Instruction.Create(OpCodes.Ldloc, returnVariable);
                instructions.Add(lastLd);
                instructions.Add(Instruction.Create(OpCodes.Ret));

                for (var index = 0; index < instructions.Count - 2; index++)
                {
                    var instruction = instructions[index];
                    if (instruction.OpCode == OpCodes.Ret)
                    {
                        instruction.OpCode = OpCodes.Leave;
                        instruction.Operand = lastLd;
                        instructions.Insert(index, Instruction.Create(OpCodes.Stloc, returnVariable));
                        index++;
                    }
                }
                return lastLd;
            }
        }

        private static Instruction FirstInstructionSkipCtor(MethodDefinition Method)
        {
            var body = Method.Body;
            if (Method.IsConstructor && !Method.IsStatic)
            {
                return body.Instructions[2];
            }
            return body.Instructions[1];
        }

        private static void InjectTryFinally(MethodDefinition method, ModuleDefinition module)
        {
            if (method.Body == null) return;
            var il = method.Body.GetILProcessor();
            var firstInstruction = FirstInstructionSkipCtor(method);

            var getMethod = il.Create(OpCodes.Call, module.ImportReference(m_getCurrentMethod));
            var getMethodStr = il.Create(OpCodes.Call, module.ImportReference(m_getMethodString));
            var beginSample = il.Create(
                OpCodes.Call,
                module.ImportReference(m_beginSampleMethod));
            il.InsertAfter(il.Body.Instructions[0], beginSample);
            il.InsertAfter(il.Body.Instructions[0], getMethodStr);
            il.InsertAfter(il.Body.Instructions[0], getMethod);

            var returnInstruction = FixReturns(method);
            var beforeReturn = Instruction.Create(OpCodes.Nop);
            il.InsertBefore(returnInstruction, beforeReturn);

            var endSample = il.Create(
                OpCodes.Call,
                module.ImportReference(m_endSampleMethod));
            il.InsertBefore(returnInstruction, endSample);
            il.InsertBefore(returnInstruction, Instruction.Create(OpCodes.Endfinally));

            var handler = new ExceptionHandler(ExceptionHandlerType.Finally)
            {
                TryStart = firstInstruction,
                TryEnd = beforeReturn,
                HandlerStart = beforeReturn,
                HandlerEnd = returnInstruction,
            };

            method.Body.ExceptionHandlers.Add(handler);
        }
    }
}

#endif