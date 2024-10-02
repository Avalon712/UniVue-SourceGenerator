using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace UniVue.SourceGenerator
{
    /// <summary>
    /// 实现思路
    /// <para>1、获取所有的EventCall注解信息</para>
    /// <para>2、生成EventManager.Invoke(EventCall) 和EventManager.RegisterEventCall信息</para>
    /// </summary>
    [Generator]
    public sealed class EventCallGenerator : ISourceGenerator
    {
        private const string EVENT_CALL_ATTRIBUTE = "UniVue.Event.EventCallAttribute";
        private const string IENUMERATOR = "System.Collections.IEnumerator";//协程返回类型
        private const string EVENT_REGISTER_ATTRIBUTE = "UniVue.Event.EventRegisterAttribute";

        private sealed class EventCallInfo
        {
            public Argument[] Arguments;
            /// <summary>
            /// 方法所在的类的全限定性类名
            /// </summary>
            public string TypeFullName;
            /// <summary>
            /// 方法名称
            /// </summary>
            public string MethodName;
            /// <summary>
            /// 是否为公共方法
            /// </summary>
            public bool IsPublic;
            /// <summary>
            /// 是否为静态方法
            /// </summary>
            public bool IsStatic;
            /// <summary>
            /// 是否为异步方法
            /// </summary>
            public bool IsAsync;
            /// <summary>
            /// 函数返回值是否为void
            /// </summary>
            public bool ReturnVoid;
            /// <summary>
            /// 方法是否为协程
            /// </summary>
            public bool IsCoroutine;
            /// <summary>
            /// 判断当前方法所在的类是否继承了MonoBehaviour
            /// </summary>
            public bool IsMonoType;
            /// <summary>
            /// 方法返回类型的全类名
            /// </summary>
            public string ReturnTypeFullName;
            public string EventName;
            public string[] Views;
        }

        private struct Argument
        {
            public SupportableArgumentType type;
            public RefKind modifier;
            public string typeFullName;
            public string argumentName;
        }

        private enum SupportableArgumentType
        {
            /// <summary>
            /// 不被支持的类型
            /// </summary>
            NotSupport,
            Int,
            Float,
            String,
            Enum,
            Bool,
            Sprite,
            Custom,
            EventUI,
            ArgumentUI,
            EventCall,
            Image,
            Slider,
            TMP_Text,
            TMP_InputField,
            Toggle,
            TMP_Dropdown,
        }

        private sealed class TypeInfo
        {
            public TypeDeclarationSyntax syntaxNode;
            public string namedspace;
            public string typeFullName;
            public string typeName;
            public bool isSealed;
            public bool isStruct;
            public bool baseHadImplInterface; //父类是否已经实现了IEventRegister接口
            public List<EventCallInfo> callInfos;
        }

        private sealed class EventRegisterSyntaxReceiver : ISyntaxContextReceiver
        {
            public Dictionary<string, TypeInfo> Calls { get; } = new Dictionary<string, TypeInfo>();

            public void OnVisitSyntaxNode(GeneratorSyntaxContext context)
            {
                if (context.Node is TypeDeclarationSyntax typeNode &&
                    typeNode.Modifiers.Any(SyntaxKind.PartialKeyword) &&
                    (typeNode.Parent is NamespaceDeclarationSyntax || typeNode.Parent is CompilationUnitSyntax))
                {
                    INamedTypeSymbol typeSymbol = context.SemanticModel.GetDeclaredSymbol(typeNode);

                    if (!HadAttribute(typeSymbol.GetAttributes(), EVENT_REGISTER_ATTRIBUTE, out var _)) return;

                    TypeInfo typeInfo = new TypeInfo();
                    typeInfo.syntaxNode = typeNode;
                    typeInfo.typeName = typeSymbol.Name;
                    typeInfo.typeFullName = typeSymbol.ToDisplayString();
                    typeInfo.isStruct = typeNode is StructDeclarationSyntax;
                    typeInfo.isSealed = typeSymbol.IsSealed || typeInfo.isStruct;
                    typeInfo.baseHadImplInterface = BaseHadImplInterface(typeSymbol);
                    typeInfo.callInfos = new List<EventCallInfo>();
                    if (typeNode.Parent is NamespaceDeclarationSyntax @namespace)
                        typeInfo.namedspace = @namespace.Name.ToString();
                    Calls.Add(typeInfo.typeFullName, typeInfo);

                    using (var it = typeNode.DescendantNodes().OfType<MethodDeclarationSyntax>().GetEnumerator())
                    {
                        while (it.MoveNext())
                        {
                            MethodDeclarationSyntax method = it.Current;
                            IMethodSymbol methodSymbol = context.SemanticModel.GetDeclaredSymbol(method);

                            if (!HadAttribute(methodSymbol.GetAttributes(), EVENT_CALL_ATTRIBUTE,out var attribute)) continue;

                            if (attribute != null)
                            {
                                //不支持泛型方法
                                //不支持参数带有params修饰
                                //不支持为不被支持的参数类型生成EventCall
                                //不支持out修饰
                                if (methodSymbol.IsGenericMethod ||
                                    methodSymbol.IsAsync || //暂不对异步方法进行支持
                                    methodSymbol.Parameters.Any(p => p.IsParams || p.RefKind == RefKind.Out || TypeFullNameToArgumentType(p.Type.ToDisplayString()) == SupportableArgumentType.NotSupport))
                                    continue;

                                EventCallInfo callInfo = new EventCallInfo();

                                callInfo.IsPublic = method.Modifiers.Any(m => m.Text == "public");
                                callInfo.IsStatic = methodSymbol.IsStatic;
                                callInfo.ReturnVoid = methodSymbol.ReturnsVoid;
                                callInfo.IsCoroutine = methodSymbol.ReturnType.ToDisplayString() == IENUMERATOR;
                                callInfo.IsAsync = methodSymbol.IsAsync;
                                callInfo.IsMonoType = IsMonoType(methodSymbol.ContainingType);

                                callInfo.MethodName = methodSymbol.Name;
                                callInfo.TypeFullName = methodSymbol.ContainingType.ToDisplayString();

                                if (!callInfo.ReturnVoid)
                                    callInfo.ReturnTypeFullName = methodSymbol.ReturnType?.ToDisplayString();

                                //[EventCall] 信息
                                if (attribute.ConstructorArguments.Length > 0)
                                {
                                    foreach (var constant in attribute.ConstructorArguments)
                                    {
                                        if (constant.Kind == TypedConstantKind.Array)
                                        {
                                            if (constant.IsNull) continue;
                                            callInfo.Views = new string[constant.Values.Length];
                                            TypedConstant[] items = constant.Values.ToArray();
                                            for (int j = 0; j < constant.Values.Length; j++)
                                            {
                                                callInfo.Views[j] = items[j].Value.ToString();
                                            }
                                        }
                                        else
                                        {
                                            if (constant.IsNull)
                                                callInfo.EventName = methodSymbol.Name;
                                            else
                                                callInfo.EventName = constant.Value.ToString();
                                        }
                                    }
                                }
                                else
                                {
                                    callInfo.EventName = methodSymbol.Name;
                                }

                                if (string.IsNullOrEmpty(callInfo.EventName))
                                    callInfo.EventName = methodSymbol.Name;

                                //方法参数列表
                                if (methodSymbol.Parameters != null && methodSymbol.Parameters.Length > 0)
                                {
                                    Argument[] arguments = new Argument[methodSymbol.Parameters.Length];
                                    for (int i = 0; i < arguments.Length; i++)
                                    {
                                        var paramter = methodSymbol.Parameters[i];
                                        Argument argument = new Argument();
                                        argument.modifier = paramter.RefKind;
                                        argument.typeFullName = paramter.Type.ToDisplayString();
                                        argument.argumentName = paramter.Name;
                                        if (paramter.Type.TypeKind == TypeKind.Enum)
                                        {
                                            argument.type = SupportableArgumentType.Enum;
                                        }
                                        else
                                        {
                                            argument.type = TypeFullNameToArgumentType(argument.typeFullName);
                                        }
                                        arguments[i] = argument;
                                    }
                                    callInfo.Arguments = arguments;
                                }

                                typeInfo.callInfos.Add(callInfo);
                            }
                        }
                    }

                }
            }

            private bool BaseHadImplInterface(INamedTypeSymbol typeSymbol)
            {
                while (typeSymbol.BaseType != null)
                {
                    typeSymbol = typeSymbol.BaseType;
                    if (HadAttribute(typeSymbol.GetAttributes(), EVENT_REGISTER_ATTRIBUTE, out var _)) return true;
                }
                return false;
            }

            private bool HadAttribute(ImmutableArray<AttributeData> attrs, string typeFullName,out AttributeData attrData)
            {
                attrData = null;
                if (attrs != null && attrs.Length > 0)
                {
                    foreach (var attr in attrs)
                    {
                        if (attr.AttributeClass?.ToDisplayString() == typeFullName)
                        {
                            attrData = attr;
                            break;
                        }
                    }
                }
                return attrData != null;
            }


            private SupportableArgumentType TypeFullNameToArgumentType(string fullName)
            {
                switch (fullName)
                {
                    case "System.Int32": return SupportableArgumentType.Int;
                    case "int": return SupportableArgumentType.Int;
                    case "System.Single": return SupportableArgumentType.Float;
                    case "float": return SupportableArgumentType.Float;
                    case "System.String": return SupportableArgumentType.String;
                    case "string": return SupportableArgumentType.String;
                    case "System.Boolean": return SupportableArgumentType.Bool;
                    case "bool": return SupportableArgumentType.Bool;
                    case "UnityEngine.Sprite": return SupportableArgumentType.Sprite;
                    case "UniVue.Event.EventUI": return SupportableArgumentType.EventUI;
                    case "UniVue.Event.ArgumentUI": return SupportableArgumentType.ArgumentUI;
                    case "UnityEngine.UI.Image": return SupportableArgumentType.Image;
                    case "UnityEngine.UI.Slider": return SupportableArgumentType.Slider;
                    case "UnityEngine.UI.Toggle": return SupportableArgumentType.Toggle;
                    case "TMPro.TMP_Text": return SupportableArgumentType.TMP_Text;
                    case "TMPro.TMP_InputField": return SupportableArgumentType.TMP_InputField;
                    case "TMPro.TMP_Dropdown": return SupportableArgumentType.TMP_Dropdown;
                    default:
                        if (fullName.StartsWith("System.Collections.Generic"))
                            return SupportableArgumentType.NotSupport;
                        return SupportableArgumentType.Custom;
                }
            }

            private bool IsMonoType(INamedTypeSymbol typeSymbol)
            {
                if (typeSymbol == null)
                    return false;

                while (typeSymbol.BaseType != null)
                {
                    string fullName = typeSymbol.ToDisplayString();
                    if ("UnityEngine.MonoBehaviour".Equals(fullName))
                    {
                        return true;
                    }
                    typeSymbol = typeSymbol.BaseType;
                }
                return false;
            }

        }

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new EventRegisterSyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            //System.IO.File.AppendAllText("F:\\assembly.txt", context.Compilation.Assembly.Name + "\n");

            if (context.SyntaxContextReceiver is EventRegisterSyntaxReceiver receiver)
            {
                if (receiver.Calls.Count > 0)
                {
                    StringBuilder source = new StringBuilder();
                    foreach (var typeInfo in receiver.Calls.Values)
                    {
                        GenerateSourceFile(typeInfo, source);
                        context.AddSource($"{typeInfo.typeFullName}.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
                        source.Clear();
                    }
                }
            }
        }

        private void GenerateSourceFile(TypeInfo typeInfo, StringBuilder source)
        {
            source.AppendLine("// <auto generated/>\n");
            source.AppendLine("using UniVue.Event;\n");
            bool haveNamespace = !string.IsNullOrEmpty(typeInfo.namedspace);
            if (haveNamespace)
            {
                source.Append("namespace ");
                source.AppendLine(typeInfo.namedspace);
                source.AppendLine("{");
            }
            StartType(haveNamespace, typeInfo, source);

            GenerateRegisterEventCallMethod(typeInfo, source);
            GenerateInvokeMethod(typeInfo, source);

            EndType(haveNamespace, source);
            if (haveNamespace)
                source.AppendLine("}");
        }

        #region 生成类声明
        private void StartType(bool haveNamespace, TypeInfo typeInfo, StringBuilder source)
        {
            if (haveNamespace)
                source.Append('\t');
            foreach (var keyword in typeInfo.syntaxNode.Modifiers)
            {
                source.Append(keyword.Text);
                source.Append(' ');
            }

            if (typeInfo.isStruct)
                source.Append("struct ");
            else
                source.Append("class ");

            source.Append(typeInfo.typeName);

            if (!typeInfo.baseHadImplInterface)
            {
                source.Append(" : ");
                source.Append("UniVue.Event.IEventRegister");
            }

            source.AppendLine();

            if (haveNamespace)
                source.Append('\t');
            source.AppendLine("{");
        }
        private void EndType(bool haveNamespace, StringBuilder source)
        {
            if (haveNamespace)
                source.Append('\t');
            source.AppendLine("}");
        }
        #endregion

        private void GenerateRegisterEventCallMethod(TypeInfo typeInfo, StringBuilder source)
        {
            bool haveNamespace = !string.IsNullOrEmpty(typeInfo.namedspace);

            source.Append(haveNamespace ? "\t\t" : "\t");

            if (typeInfo.isStruct || (typeInfo.isSealed && !typeInfo.baseHadImplInterface))
                source.AppendLine("void UniVue.Event.IEventRegister.OnRegisterEventCall(System.Collections.Generic.List<EventCall> calls)");
            else if (!typeInfo.isSealed && !typeInfo.baseHadImplInterface)
                source.AppendLine("public virtual void OnRegisterEventCall(System.Collections.Generic.List<EventCall> calls)");
            else if (typeInfo.baseHadImplInterface)
                source.AppendLine("public override void OnRegisterEventCall(System.Collections.Generic.List<EventCall> calls)");

            source.Append(haveNamespace ? "\t\t" : "\t");
            source.AppendLine("{");

            if (typeInfo.baseHadImplInterface)
            {
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("base.OnRegisterEventCall(calls);");
            }
            int callCount = 0;
            foreach (var callInfo in typeInfo.callInfos)
            {
                /*
        public EventCall(Argument[] arguments,
                         string eventName,
                         string[] views,
                         string methodName,
                         string typeFullName,
                         bool isCoroutine)
                     */

                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.Append("EventCall call");
                source.Append(callCount);
                source.Append(" = new EventCall(");

                //arg0 - Argument[] arguments
                if (callInfo.Arguments != null)
                {
                    source.Append("new Argument[");
                    source.Append(callInfo.Arguments.Length);
                    source.Append("]{");
                    for (int j = 0; j < callInfo.Arguments.Length; j++)
                    {
                        /*
public Argument(string typeFullName, SupportableArgumentType type, string name, object value)
        {
            this.typeFullName = typeFullName;
            this.type = type;
            this.name = name;
            this.value = value;
        }
                         */
                        var arg = callInfo.Arguments[j];
                        source.Append("new Argument(");
                        source.Append('\"');
                        source.Append(arg.typeFullName);
                        source.Append('\"');
                        source.Append(", ");
                        source.Append("SupportableArgumentType.");
                        source.Append(arg.type.ToString());
                        source.Append(", ");
                        source.Append('\"');
                        source.Append(arg.argumentName);
                        source.Append('\"');
                        source.Append(")");
                        if (j != callInfo.Arguments.Length - 1)
                        {
                            source.Append(',');
                        }
                    }
                    source.Append("}, ");
                }
                else
                {
                    source.Append("null, ");
                }

                //arg1 - string eventName
                source.Append('\"');
                source.Append(callInfo.EventName);
                source.Append('\"');
                source.Append(", ");

                //arg2 - string[] views
                if (callInfo.Views != null)
                {
                    source.Append("new string[");
                    source.Append(callInfo.Views.Length);
                    source.Append("]{");
                    for (int j = 0; j < callInfo.Views.Length; j++)
                    {
                        source.Append('\"');
                        source.Append(callInfo.Views[j]);
                        source.Append('\"');
                        if (j != callInfo.Views.Length - 1)
                        {
                            source.Append(',');
                        }
                    }
                    source.Append("}, ");
                }
                else
                {
                    source.Append("null, ");
                }

                //arg3 - string methodName
                source.Append('\"');
                source.Append(callInfo.MethodName);
                source.Append('\"');
                source.Append(", ");

                //arg4 - string typeFullName
                source.Append('\"');
                source.Append(callInfo.TypeFullName);
                source.Append('\"');
                source.Append(", ");

                //arg5 - bool isCoroutine
                source.Append(callInfo.IsCoroutine ? "true" : "false");
                source.AppendLine(");");

                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.Append("calls.Add(call");
                source.Append(callCount);
                source.AppendLine(");");

                callCount++;
            }


            source.Append(haveNamespace ? "\t\t" : "\t");
            source.AppendLine("}");
        }

        private void GenerateInvokeMethod(TypeInfo typeInfo, StringBuilder source)
        {
            bool haveNamespace = !string.IsNullOrEmpty(typeInfo.namedspace);
            source.Append(haveNamespace ? "\t\t" : "\t");

            if (typeInfo.isStruct || (typeInfo.isSealed && !typeInfo.baseHadImplInterface))
                source.AppendLine("object UniVue.Event.IEventRegister.Invoke(UniVue.Event.EventCall call)");
            else if (!typeInfo.isSealed && !typeInfo.baseHadImplInterface)
                source.AppendLine("public virtual object Invoke(UniVue.Event.EventCall call)");
            else if (typeInfo.baseHadImplInterface)
                source.AppendLine("public override object Invoke(UniVue.Event.EventCall call)");

            source.Append(haveNamespace ? "\t\t" : "\t");
            source.AppendLine("{");

            source.Append(haveNamespace ? "\t\t\t" : "\t\t");
            source.Append("if(call.TypeFullName == ");
            source.Append('\"');
            source.Append(typeInfo.typeFullName);
            source.Append('\"');
            source.AppendLine(")");
            source.Append(haveNamespace ? "\t\t\t" : "\t\t");
            source.AppendLine("{");

            source.Append(haveNamespace ? "\t\t\t\t" : "\t\t\t");
            source.AppendLine("switch(call.MethodName)");
            source.Append(haveNamespace ? "\t\t\t\t" : "\t\t\t");
            source.AppendLine("{");

            foreach (var call in typeInfo.callInfos)
            {
                if (call.IsCoroutine && !call.IsMonoType) continue;

                source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                source.Append("case ");
                source.Append('\"');
                source.Append(call.MethodName);
                source.Append('\"');
                source.AppendLine(":");
                source.Append(haveNamespace ? "\t\t\t\t\t\t" : "\t\t\t\t\t");
                source.AppendLine("{");

                for (int i = 0; call.Arguments != null && i < call.Arguments.Length; i++)
                {
                    source.Append(haveNamespace ? "\t\t\t\t\t\t\t" : "\t\t\t\t\t\t");
                    source.Append(call.Arguments[i].typeFullName);
                    source.Append(" arg");
                    source.Append(i);
                    source.Append(" = (");
                    source.Append(call.Arguments[i].typeFullName);
                    source.Append(")call.Arguments[");
                    source.Append(i);
                    source.AppendLine("].value;");
                }

                if (call.IsCoroutine && call.IsMonoType)
                {
                    source.Append(haveNamespace ? "\t\t\t\t\t\t\t" : "\t\t\t\t\t\t");
                    source.Append("StartCoroutine(");
                    source.Append(call.MethodName);
                    source.Append('(');

                    for (int i = 0; call.Arguments != null && i < call.Arguments.Length; i++)
                    {
                        Argument argument = call.Arguments[i];
                        if (argument.modifier == RefKind.Ref)
                        {
                            source.Append("ref ");
                        }
                        source.Append("arg");
                        source.Append(i);
                        if (i != call.Arguments.Length - 1)
                            source.Append(", ");
                    }

                    source.AppendLine("));");
                    source.Append(haveNamespace ? "\t\t\t\t\t\t\t" : "\t\t\t\t\t\t");
                    source.AppendLine("return null;");

                    source.Append(haveNamespace ? "\t\t\t\t\t\t" : "\t\t\t\t\t");
                    source.AppendLine("}");
                }
                else
                {
                    source.Append(haveNamespace ? "\t\t\t\t\t\t\t" : "\t\t\t\t\t\t");
                    source.Append(call.ReturnVoid ? "" : "object result = ");
                    source.Append(call.MethodName);
                    source.Append('(');

                    for (int i = 0; call.Arguments != null && i < call.Arguments.Length; i++)
                    {
                        Argument argument = call.Arguments[i];
                        if (argument.modifier == RefKind.Ref)
                        {
                            source.Append("ref ");
                        }
                        source.Append("arg");
                        source.Append(i);
                        if (i != call.Arguments.Length - 1)
                            source.Append(", ");
                    }

                    source.AppendLine(");");
                    source.Append(haveNamespace ? "\t\t\t\t\t\t\t" : "\t\t\t\t\t\t");
                    source.Append("return ");
                    source.AppendLine(call.ReturnVoid ? "null;" : "result;");

                    source.Append(haveNamespace ? "\t\t\t\t\t\t" : "\t\t\t\t\t");
                    source.AppendLine("}");
                }
            }

            source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
            source.AppendLine("default: return null;");

            source.Append(haveNamespace ? "\t\t\t\t" : "\t\t\t");
            source.AppendLine("}");

            source.Append(haveNamespace ? "\t\t\t" : "\t\t");
            source.AppendLine("}");
            source.Append(haveNamespace ? "\t\t\t" : "\t\t");
            source.Append("else return ");
            source.AppendLine(typeInfo.baseHadImplInterface ? "base.Invoke(call);" : "null;");

            source.Append(haveNamespace ? "\t\t" : "\t");
            source.AppendLine("}");
        }

    }
}
