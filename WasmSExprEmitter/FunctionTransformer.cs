﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using JSIL;
using JSIL.Ast;
using JSIL.Compiler.Extensibility;
using JSIL.Internal;
using Mono.Cecil;

namespace WasmSExprEmitter {
    public class FunctionTransformer : IFunctionTransformer {
        internal FunctionTransformer () {
        }

        public void InitializeTransformPipeline (AssemblyTranslator translator, FunctionTransformPipeline transformPipeline) {
        }

        private static bool ExtractLiteral<T> (JSExpression expr, out T result) {
            result = default(T);

            var literal = expr as JSLiteral;
            if (literal == null) {
                // Console.WriteLine("ExtractLiteral<{0}> got non-literal {1}", typeof(T).FullName, expr);
                return false;
            }

            try {
                result = (T)Convert.ChangeType(literal.Literal, typeof(T));
                return true;
            } catch (Exception exception) {
                Console.WriteLine("ExtractLiteral<{0}> failed {1}", typeof(T).FullName, exception.Message);
                return false;
            }
        }

        private static JSExpression Add (TypeSystem typeSystem, JSExpression lhs, JSExpression rhs) {
            long iLhs, iRhs;
            if (ExtractLiteral(lhs, out iLhs) && ExtractLiteral(rhs, out iRhs))
                return JSLiteral.New((int)(iLhs + iRhs));

            var type = lhs.GetActualType(typeSystem);
            return new JSBinaryOperatorExpression(JSOperator.Add, lhs, rhs, type);
        }

        private static JSExpression Mul (TypeSystem typeSystem, JSExpression lhs, JSExpression rhs) {
            long iLhs, iRhs;
            if (ExtractLiteral(lhs, out iLhs) && ExtractLiteral(rhs, out iRhs))
                return JSLiteral.New((int)(iLhs * iRhs));

            return new JSBinaryOperatorExpression(JSOperator.Multiply, lhs, rhs, typeSystem.Int32);
        }

        private JSExpression[] UnpackArgsArray (JSExpression expr) {
            JSExpression[] result;

            var invoke = expr as JSInvocationExpression;
            if (invoke != null) {
                var m = invoke.JSMethod.Reference;
                if ((m.Name == "Empty") && (m.DeclaringType.FullName == "System.Array"))
                    return new JSExpression[0];

                throw new Exception("Unhandled invocation as args array: " + invoke);
            }

            var argumentsLiteral = expr as JSNewArrayExpression;
            if (argumentsLiteral == null)
                throw new Exception("Expected arguments array but got " + expr.GetType().Name);

            var initializer = argumentsLiteral.SizeOrArrayInitializer;
            if (initializer is JSLiteral) {
                // HACK: Is this right?
                long size;
                if (ExtractLiteral<long>(initializer, out size))
                    result = new JSExpression[(int)size];
                else
                    result = new JSExpression[0];
            } else if (!(initializer is JSArrayExpression)) {
                throw new Exception("Expected either size literal or array expression");
            } else {
                var argumentsArray = (JSArrayExpression)initializer;

                // HACK: Values used as initializers may be references for no particularly good reason.
                // Just unfold them to values.
                result = argumentsArray.Values.Select(
                    (arg) => {
                        var re = arg as JSReferenceExpression;
                        if (re != null)
                            return re.Referent;
                        else
                            return arg;
                    }
                ).ToArray();
            }

            return result;
        }

        public JSExpression MaybeReplaceMethodCall (MethodReference caller, MethodReference method, MethodInfo methodInfo, JSExpression thisExpression, JSExpression[] arguments, TypeReference resultType, bool explicitThis) {
            var typeSystem = method.Module.TypeSystem;
            var fullName = method.FullName;

            switch (fullName) {
                case "System.Double System.Math::Sqrt(System.Double)":
                    return new AbstractSExpression(
                        "f64.sqrt",
                        typeSystem.Double,
                        arguments,
                        isConstantIfArgumentsAre: true
                    );

                case "System.Double System.Math::Floor(System.Double)":
                    return new AbstractSExpression(
                        "f64.floor",
                        typeSystem.Double,
                        arguments,
                        isConstantIfArgumentsAre: true
                    );

                case "System.Double System.Math::Ceiling(System.Double)":
                    return new AbstractSExpression(
                        "f64.ceil",
                        typeSystem.Double,
                        arguments,
                        isConstantIfArgumentsAre: true
                    );

                case "System.Void Wasm.Test::Printf(System.String,System.Object[])":
                    // HACK: Ignored for now
                    return new JSNullExpression();

                case "System.Void Wasm.Test::Invoke(System.String,System.Object[])": {
                    var literalName = (JSStringLiteral)arguments[0];
                    var argumentValues = UnpackArgsArray(arguments[1]);

                    return new InvokeExport(
                        literalName.Value, argumentValues
                    );
                }

                case "System.Void Wasm.Test::AssertEq(System.Object,System.String,System.Object[])": {
                    var expected = arguments[0];
                    string methodName;
                    if (!ExtractLiteral(arguments[1], out methodName))
                        throw new Exception("Expected export name as arg1 of asserteq");
                    var invokeArguments = UnpackArgsArray(arguments[2]);

                    return new AssertEq(expected, methodName, invokeArguments);
                }

                case "System.Void Wasm.Test::AssertHeapEq(System.Int32,System.String)": {
                    int offset;
                    if (!ExtractLiteral(arguments[0], out offset))
                        throw new Exception("Expected offset as arg0 of assertheapeq");

                    string expected;
                    if (!ExtractLiteral(arguments[1], out expected))
                        throw new Exception("Expected expected as arg1 of assertheapeq");
                    return new AssertHeapEq(offset, expected);
                }

                case "System.Void Wasm.Test::AssertHeapEqFile(System.Int32,System.Int32,System.String)": {
                    int offset;
                    if (!ExtractLiteral(arguments[0], out offset))
                        throw new Exception("Expected offset as arg0 of assertheapeqfile");

                    int count;
                    if (!ExtractLiteral(arguments[1], out count))
                        throw new Exception("Expected count as arg1 of assertheapeqfile");

                    string fileName;
                    if (!ExtractLiteral(arguments[2], out fileName))
                        throw new Exception("Expected fileName as arg2 of assertheapeqfile");

                    var actualPath = Path.Combine("third_party", "test_data", fileName);
                    if (!File.Exists(actualPath))
                        throw new FileNotFoundException("Test expects result contained in nonexistent file", actualPath);

                    var expectedBytes = File.ReadAllBytes(actualPath);
                    if (count != expectedBytes.Length)
                        throw new Exception("Size of expected data file '" + actualPath + "' does not match count argument " + count);

                    var sb = new StringBuilder();
                    for (var i = 0; i < count; i++)
                        sb.Append((char)expectedBytes[i]);

                    var expected = sb.ToString();
                    return new AssertHeapEq(offset, expected);
                }

                case "System.Void Wasm.Heap::SetHeapSize(System.Int32)": {
                    var td = caller.DeclaringType.Resolve();
                    var hs = WasmUtil.HeapSizes;
                    if (hs.ContainsKey(td))
                        throw new Exception("Heap size for type " + td.FullName + " already set");

                    long heapSize;
                    if (!ExtractLiteral(arguments[0], out heapSize))
                        throw new ArgumentException("SetHeapSize's argument must be an int literal");

                    hs.Add(td, (int)heapSize);

                    return new JSNullExpression();
                }

                case "System.Int32 Wasm.HeapI32::get_Item(System.Int32)":
                case "System.Int32 Wasm.HeapI32::get_Item(System.Int32,System.Int32)": {
                    JSExpression actualAddress;
                    if (arguments.Length == 2)
                        actualAddress = Add(typeSystem, arguments[0], arguments[1]);
                    else
                        actualAddress = arguments[0];

                    // HACK: Indices are in elements, not bytes
                    var actualAddressBytes = Mul(typeSystem, actualAddress, JSLiteral.New(4));

                    return new GetMemory(typeSystem.Int32, false, actualAddressBytes);
                }

                case "System.Byte Wasm.HeapU8::get_Item(System.Int32)":
                case "System.Byte Wasm.HeapU8::get_Item(System.Int32,System.Int32)": {
                    JSExpression actualAddress;
                    if (arguments.Length == 2)
                        actualAddress = Add(typeSystem, arguments[0], arguments[1]);
                    else
                        actualAddress = arguments[0];

                    return new GetMemory(typeSystem.Byte, false, actualAddress);
                }

                case "System.Void Wasm.HeapI32::set_Item(System.Int32,System.Int32)":
                case "System.Void Wasm.HeapI32::set_Item(System.Int32,System.Int32,System.Int32)": {
                    JSExpression actualAddress;
                    if (arguments.Length == 3)
                        actualAddress = Add(typeSystem, arguments[0], arguments[1]);
                    else
                        actualAddress = arguments[0];

                    // HACK: Indices are in elements, not bytes
                    var actualAddressBytes = Mul(typeSystem, actualAddress, JSLiteral.New(4));

                    return new SetMemory(typeSystem.Int32, false, actualAddressBytes, arguments[arguments.Length - 1]);
                }

                case "System.Void Wasm.HeapU8::set_Item(System.Int32,System.Byte)":
                case "System.Void Wasm.HeapU8::set_Item(System.Int32,System.Int32,System.Byte)": {
                    JSExpression actualAddress;
                    if (arguments.Length == 3)
                        actualAddress = Add(typeSystem, arguments[0], arguments[1]);
                    else
                        actualAddress = arguments[0];

                    return new SetMemory(typeSystem.Byte, false, actualAddress, arguments[arguments.Length - 1]);
                }

                case "System.Char System.String::get_Chars(System.Int32)": {
                    var actualAddress = Add(typeSystem, thisExpression, arguments[0]);
                    return new GetMemory(
                        typeSystem.Byte, false, actualAddress
                    );
                }

                case "System.Int32 System.String::get_Length()": {
                    return new GetStringLength(thisExpression);
                }

                case "System.Int32* Wasm.HeapI32::get_Base()":
                case "System.Byte* Wasm.HeapU8::get_Base()": {
                    return JSLiteral.DefaultValue(method.ReturnType);
                }
            }

            if (false)
                Console.WriteLine("// Treating method '{0}' as runtime call", fullName);

            return null;
        }
    }
}
