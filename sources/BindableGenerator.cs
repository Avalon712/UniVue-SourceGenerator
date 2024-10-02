using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UniVue.SourceGenerator
{
    /// <summary>
    /// 实现思路
    /// <para>1.找到所有标记有[Bindable]特性的类</para>
    /// <para>2.获取所有可绑定的字段</para>
    /// <para>3.对每个字段进行处理（生成属性、[DontNotify]、[AlsoNotify]等)</para>
    /// </summary>
    [Generator]
    public sealed class BindableGenerator : ISourceGenerator
    {
        private const string BINDABLE = "UniVue.Model.BindableAttribute";
        private const string ALSO_NOTIFY = "UniVue.Model.AlsoNotifyAttribute";
        private const string DONT_NOTIFY = "UniVue.Model.DontNotifyAttribute";
        private const string PROPERTY_NAME = "UniVue.Model.PropertyNameAttribute";
        private const string BINDABLE_INTERFACE = "UniVue.Model.IBindableModel";
        private const string CODE_INJECT = "UniVue.Model.CodeInjectAttribute";

        /// <summary>
        /// 所有可绑定的枚举的类型
        /// </summary>
        private Dictionary<string, ITypeSymbol> _enums;
        private Dictionary<string, bool> _enumGenerated;

        private sealed class PropertyInfo
        {
            public string fieldName;
            public string propertyName;
            public bool isEnumType;
            public string typeFullName;
            public List<string> alsoNotifyOther;
            public string comment;
            public TypeKind kind;
            public SpecialType specialType;
            public BindableType bindType;
            public List<CodeInjectInfo> codes;
            public bool isInherit;
        }

        private sealed class CodeInjectInfo
        {
            public InjectType type;
            public string[] codes;
        }

        private sealed class TypeInfo
        {
            public TypeDeclarationSyntax syntaxNode;
            public INamedTypeSymbol typeSymbol;
            public string typeName;
            public string typeFullName;
            public string namedspace;
            public bool isSealed;
            public bool isStruct;
            public bool useEvent;
            public bool baseHadImplInterface; //父类是否已经实现了IBindableModel接口
            public bool baseUseEvent;
            public List<string> baseTypeFullNames;
            public PropertyInfo[] properties; //字段生成的属性
        }

        private sealed class BindableSyntaxReceiver : ISyntaxContextReceiver
        {
            /// <summary>
            /// key = typeFullName
            /// </summary>
            public Dictionary<string, TypeInfo> TypeInfos { get; } = new Dictionary<string, TypeInfo>();

            public void OnVisitSyntaxNode(GeneratorSyntaxContext context)
            {
                try
                {
                    if (context.Node is TypeDeclarationSyntax node && TryGenerateTypeInfo(context, node, out TypeInfo typeInfo))
                    {
                        TypeInfos.Add(typeInfo.typeFullName, typeInfo);
                    }
                }
                catch (System.Exception e)
                {
                    //var exceptionDetails = new System.IO.StringWriter();
                    //exceptionDetails.WriteLine("异常类型: " + e.GetType().FullName);
                    //exceptionDetails.WriteLine("异常消息: " + e.Message);
                    //exceptionDetails.WriteLine("堆栈跟踪: " + e.StackTrace);
                    //System.IO.File.WriteAllText("F:\\log1.txt", exceptionDetails.ToString());
                }
            }

            private bool TryGenerateTypeInfo(in GeneratorSyntaxContext context, TypeDeclarationSyntax node, out TypeInfo typeInfo)
            {
                typeInfo = null;
                INamedTypeSymbol typeSymbol = context.SemanticModel.GetDeclaredSymbol(node);

                if (HadBindableAttribute(typeSymbol, out AttributeData bindableAttribute) &&
                    node.Modifiers.Any(SyntaxKind.PartialKeyword) &&
                    (node.Parent is NamespaceDeclarationSyntax ||
                     node.Parent is CompilationUnitSyntax))
                {
                    typeInfo = new TypeInfo();
                    typeInfo.typeSymbol = typeSymbol;
                    typeInfo.syntaxNode = node;
                    typeInfo.isStruct = node is StructDeclarationSyntax;
                    if (!typeInfo.isStruct)
                        typeInfo.baseHadImplInterface = BaseHadImplInterface(typeSymbol);
                    typeInfo.typeName = typeSymbol.Name;
                    typeInfo.typeFullName = typeSymbol.ToDisplayString();
                    if (node.Parent is NamespaceDeclarationSyntax @namespace)
                        typeInfo.namedspace = @namespace.Name.ToString();
                    typeInfo.isSealed = typeSymbol.IsSealed || typeInfo.isStruct;
                    typeInfo.baseUseEvent = BaseUseEvent(typeSymbol);
                    var args = bindableAttribute.NamedArguments;
                    if (args != null && args.Length > 0)
                        bool.TryParse(args.Where(t => t.Key == "OnPropertyChanged")?.Single().Value.Value.ToString(), out typeInfo.useEvent);

                    typeInfo.baseTypeFullNames = BaseList(typeSymbol);
                }

                return typeInfo != null;
            }

            /// <summary>
            /// 不含有接口
            /// </summary>
            private List<string> BaseList(INamedTypeSymbol typeSymbol)
            {
                List<string> bases = new List<string>();
                while (typeSymbol.BaseType != null)
                {
                    typeSymbol = typeSymbol.BaseType;
                    if (typeSymbol.TypeKind == TypeKind.Class)
                        bases.Add(typeSymbol.ToDisplayString());
                }
                return bases;
            }

            private bool BaseHadImplInterface(INamedTypeSymbol typeSymbol)
            {
                while (typeSymbol.BaseType != null)
                {
                    typeSymbol = typeSymbol.BaseType;
                    if (HadBindableAttribute(typeSymbol, out var _)) return true;
                }
                return false;
            }

            private bool HadBindableAttribute(INamedTypeSymbol typeSymbol, out AttributeData bindableAttribute)
            {
                var attrs = typeSymbol.GetAttributes();
                bindableAttribute = null;
                if (attrs != null && attrs.Length > 0)
                {
                    foreach (var attr in attrs)
                    {
                        if (attr.AttributeClass?.ToDisplayString() == BINDABLE)
                        {
                            bindableAttribute = attr;
                            break;
                        }
                    }
                }
                return bindableAttribute != null;
            }

            private bool BaseUseEvent(INamedTypeSymbol typeSymbol)
            {
                while (typeSymbol.BaseType != null)
                {
                    typeSymbol = typeSymbol.BaseType;
                    if (HadBindableAttribute(typeSymbol, out var attr))
                    {
                        return attr.NamedArguments != null && attr.NamedArguments.Length > 0;
                    }
                }
                return false;
            }
        }
        private enum InjectType
        {
            Get,
            Set_BeforeChanged,
            Set_AfterChanged,
        }

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new BindableSyntaxReceiver());
            _enums = new Dictionary<string, ITypeSymbol>();
            _enumGenerated = new Dictionary<string, bool>();
        }

        public void Execute(GeneratorExecutionContext context)
        {
            try
            {
                if (context.SyntaxContextReceiver is BindableSyntaxReceiver receiver && receiver.TypeInfos.Count > 0)
                {
                    Dictionary<string, TypeInfo> typeInfos = receiver.TypeInfos;
                    List<PropertyInfo> propertyInfos = new List<PropertyInfo>(20);

                    //获取每个类型的属性信息
                    foreach (var typeInfo in typeInfos.Values)
                    {
                        List<FieldDeclarationSyntax> fields = typeInfo.syntaxNode.Members.Where(member => member is FieldDeclarationSyntax).Select(m => m as FieldDeclarationSyntax).ToList();
                        GetPropertyInfosFromSelfFields(typeInfos, propertyInfos, typeInfo.namedspace != null, fields, context.Compilation);
                        typeInfo.properties = propertyInfos.ToArray();
                        propertyInfos.Clear();
                    }

                    StringBuilder source = new StringBuilder();
                    foreach (var typeInfo in typeInfos.Values)
                    {
                        string clazzName = ProcessType(typeInfos, propertyInfos, typeInfo, context.Compilation, source);
                        string file = source.ToString();
                        if (!string.IsNullOrEmpty(clazzName) && !string.IsNullOrWhiteSpace(file))
                        {
                            //System.IO.File.WriteAllText($"F:\\{clazzName}.txt",source.ToString());
                            context.AddSource($"{typeInfo.typeFullName}.g.cs", SourceText.From(file, Encoding.UTF8));
                        }
                        source.Clear();
                        propertyInfos.Clear();
                    }

                    _enums.Clear();
                    _enumGenerated.Clear();
                }
            }
            catch (System.Exception e)
            {
                //System.IO.File.WriteAllText("F:\\log2.txt", e.Message);
            }

        }

        private string ProcessType(Dictionary<string, TypeInfo> typeInfos, List<PropertyInfo> propertyInfos, TypeInfo typeInfo, Compilation compilation, StringBuilder source)
        {
            propertyInfos.AddRange(typeInfo.properties); //自身属性

            if (propertyInfos.Count > 0)
            {
                source.AppendLine("// <auto generated/>\n");

                GetPropertyInfosFromBase(typeInfos, propertyInfos, typeInfo, compilation);

                bool haveNamespace = StartNamespace(typeInfo, source);
                StartType(haveNamespace, typeInfo, source);

                GenerateBindableInfo(haveNamespace, typeInfo, propertyInfos, source);

                bool generateEvent = AddEvent(haveNamespace, typeInfo, source);

                //生成静态构造函数，在静态构造函数中如果有枚举类型的绑定则添加到Enums中
                AddEnumInfo(typeInfo, source);

                for (int i = 0; i < propertyInfos.Count; i++)
                {
                    if (!propertyInfos[i].isInherit)
                        AddProperty(haveNamespace, generateEvent, propertyInfos[i], source, propertyInfos);
                }

                //废弃，可以用Vue.UpdateView(IBindable model)替代
                //AddRenderViewMethod(haveNamespace, typeInfo, propertyInfos, source);

                AddUpdateModelMethod(haveNamespace, typeInfo, propertyInfos, source);
                AddConsumeableModelMethod(haveNamespace, typeInfo, source);
                AddConsumeableModelAllMethod(haveNamespace, typeInfo, source);

                EndType(haveNamespace, source);
                EndNamespace(haveNamespace, source);
            }
            return typeInfo.typeName;
        }

        #region 获取所有可绑定的属性信息

        private void GetPropertyInfosFromSelfFields(Dictionary<string, TypeInfo> typeInfos, List<PropertyInfo> propertyInfos, bool haveNamespace, List<FieldDeclarationSyntax> fields, Compilation compilation)
        {
            foreach (var field in fields)
            {
                IFieldSymbol fieldSymbol = compilation.GetSemanticModel(field.SyntaxTree).GetDeclaredSymbol(field.Declaration.Variables.First()) as IFieldSymbol;

                if (!IsSupportType(fieldSymbol.Type.ToDisplayString(), compilation)) continue;

                PropertyInfo propertyInfo = new PropertyInfo();
                propertyInfo.fieldName = fieldSymbol.Name;
                propertyInfo.typeFullName = fieldSymbol.Type.ToDisplayString();
                propertyInfo.isEnumType = fieldSymbol.Type.BaseType.SpecialType == SpecialType.System_Enum;
                propertyInfo.comment = GetComment(haveNamespace, fieldSymbol.GetDocumentationCommentXml());
                propertyInfo.kind = fieldSymbol.Type.TypeKind;
                propertyInfo.specialType = fieldSymbol.Type.SpecialType;
                propertyInfo.bindType = GetBindableType(fieldSymbol.Type, propertyInfo.typeFullName);

                var attributes = fieldSymbol.GetAttributes();
                bool dontNotify = false;
                if (attributes.Length > 0)
                {
                    foreach (var attribute in attributes)
                    {
                        switch (attribute.AttributeClass.ToDisplayString())
                        {
                            case PROPERTY_NAME:
                                propertyInfo.propertyName = attribute.ConstructorArguments.FirstOrDefault().Value.ToString();
                                break;
                            case ALSO_NOTIFY:
                                if (propertyInfo.alsoNotifyOther == null)
                                    propertyInfo.alsoNotifyOther = new List<string>();
                                propertyInfo.alsoNotifyOther.Add(attribute.ConstructorArguments.FirstOrDefault().Value.ToString());
                                break;
                            case DONT_NOTIFY:
                                dontNotify = true;
                                break;
                            case CODE_INJECT:
                                {
                                    //System.IO.File.WriteAllText(@"F:\test.txt", attribute.ConstructorArguments[0].Value.ToString() + "\n" + attribute.ConstructorArguments[1].Value.ToString());

                                    if (propertyInfo.codes == null)
                                        propertyInfo.codes = new List<CodeInjectInfo>();
                                    CodeInjectInfo code = new CodeInjectInfo();
                                    code.type = (InjectType)(int)attribute.ConstructorArguments[0].Value;
                                    code.codes = attribute.ConstructorArguments[1].Value.ToString().Split(';');
                                    propertyInfo.codes.Add(code);
                                }
                                break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(propertyInfo.propertyName))
                    propertyInfo.propertyName = GetPropertyName(fieldSymbol.Name);

                if (!dontNotify)
                {
                    propertyInfos.Add(propertyInfo);
                    if (_enums != null && propertyInfo.isEnumType && !_enums.ContainsKey(propertyInfo.typeFullName))
                    {
                        _enums.Add(propertyInfo.typeFullName, fieldSymbol.Type);
                    }
                }
            }
        }

        private void GetPropertyInfosFromBase(Dictionary<string, TypeInfo> typeInfos, List<PropertyInfo> propertyInfos, TypeInfo typeInfo, Compilation compilation)
        {
            INamedTypeSymbol typeSymbol = typeInfo.typeSymbol;
            //用户自己写的属性
            while (typeSymbol.BaseType != null)
            {
                if (typeSymbol.BaseType.TypeKind == TypeKind.Class)
                {
                    var members = typeSymbol.GetMembers();
                    if (members != null && members.Length > 0)
                    {
                        IPropertySymbol[] propertySymbols = members.Where(symbol => symbol is IPropertySymbol p &&
                        p.GetMethod != null &&
                        (p.GetMethod.DeclaredAccessibility == Accessibility.Public || p.GetMethod.DeclaredAccessibility == Accessibility.Protected)
                        && IsSupportType(p.Type.ToDisplayString(), compilation)).OfType<IPropertySymbol>().ToArray();

                        if (propertySymbols != null)
                        {
                            for (int i = 0; i < propertySymbols.Length; i++)
                            {
                                IPropertySymbol propertySymbol = propertySymbols[i];

                                if (propertyInfos.Exists(p => p.propertyName == propertySymbol.Name)) continue;

                                PropertyInfo propertyInfo = new PropertyInfo();
                                propertyInfo.propertyName = propertySymbol.Name;
                                propertyInfo.typeFullName = propertySymbol.Type.ToDisplayString();
                                propertyInfo.bindType = GetBindableType(propertySymbol.Type, propertyInfo.typeFullName);
                                propertyInfo.isInherit = true;
                                propertyInfos.Add(propertyInfo);
                            }
                        }
                    }

                }
                typeSymbol = typeSymbol.BaseType;
            }

            //源生成器生成的属性
            for (int i = 0; i < typeInfo.baseTypeFullNames.Count; i++)
            {
                if (typeInfos.TryGetValue(typeInfo.baseTypeFullNames[i], out TypeInfo baseInfo))
                {
                    for (int j = 0; j < baseInfo.properties.Length; j++)
                    {
                        PropertyInfo propertyInfo = new PropertyInfo();
                        propertyInfo.propertyName = baseInfo.properties[j].propertyName;
                        propertyInfo.typeFullName = baseInfo.properties[j].typeFullName;
                        propertyInfo.bindType = baseInfo.properties[j].bindType;
                        propertyInfo.isInherit = true;
                        propertyInfos.Add(propertyInfo);
                    }
                }
            }
        }

        private string GetComment(bool haveNamespace, string comment)
        {
            string[] strs = comment.Split('\n');
            StringBuilder builder = new StringBuilder();
            for (int i = 1; i < strs.Length - 2; i++)
            {
                builder.Append(haveNamespace ? "\t\t" : "\t");
                builder.Append("/// ");
                builder.AppendLine(strs[i].Trim());
            }
            return builder.ToString();
        }

        private bool IsSupportType(string typeFullName, Compilation compilation)
        {
            switch (typeFullName)
            {
                case "System.Int32": return true;
                case "System.String": return true;
                case "System.Single": return true;
                case "System.Boolean": return true;
                case "UnityEngine.Sprite": return true;
                case "System.Collections.Generic.List<System.Int32>": return true;
                case "System.Collections.Generic.List<System.String>": return true;
                case "System.Collections.Generic.List<System.Single>": return true;
                case "System.Collections.Generic.List<System.Boolean>": return true;
                case "System.Collections.Generic.List<UnityEngine.Sprite>": return true;
                case "int": return true;
                case "string": return true;
                case "float": return true;
                case "bool": return true;
                case "System.Collections.Generic.List<int>": return true;
                case "System.Collections.Generic.List<string>": return true;
                case "System.Collections.Generic.List<float>": return true;
                case "System.Collections.Generic.List<bool>": return true;
            }
            if (typeFullName.StartsWith("System.Collections.Generic.List<"))
            {
                string enumStr = typeFullName.Replace("System.Collections.Generic.List<", string.Empty);
                enumStr = enumStr.Replace(">", string.Empty);
                if (compilation.GetTypeByMetadataName(enumStr).BaseType.SpecialType == SpecialType.System_Enum)
                {
                    return true;
                }
            }
            else if (compilation.GetTypeByMetadataName(typeFullName)?.BaseType.SpecialType == SpecialType.System_Enum)
            {
                return true;
            }
            return false;
        }

        private string GetPropertyName(string fieldName)
        {
            if (fieldName.StartsWith("_"))
                fieldName = fieldName.TrimStart('_');
            else if (fieldName.StartsWith("m_"))
                fieldName = fieldName.TrimStart('m', '_');

            if (fieldName.Length == 0)
                return string.Empty;

            if (fieldName.Length == 1)
                return fieldName.ToUpper();

            return fieldName.Substring(0, 1).ToUpper() + fieldName.Substring(1);
        }

        private BindableType GetBindableType(ITypeSymbol typeSymbol, string typeFullName)
        {
            switch (typeFullName)
            {
                case "System.Int32":
                case "int": return BindableType.Int;
                case "System.Single":
                case "float": return BindableType.Float;
                case "System.String":
                case "string": return BindableType.String;
                case "System.Boolean":
                case "bool": return BindableType.Bool;
                case "UnityEngine.Sprite": return BindableType.Sprite;
                case "System.Collections.Generic.List<bool>": return BindableType.ListBool;
                case "System.Collections.Generic.List<int>": return BindableType.ListInt;
                case "System.Collections.Generic.List<float>": return BindableType.ListFloat;
                case "System.Collections.Generic.List<string>": return BindableType.ListString;
                case "System.Collections.Generic.List<UnityEngine.Sprite>": return BindableType.ListSprite;
                default:
                    if (typeSymbol.TypeKind == TypeKind.Enum)
                    {
                        var attrs = typeSymbol.GetAttributes();
                        if (attrs != null && attrs.Length >= 0 && attrs.Any(attr => attr.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
                        {
                            return BindableType.FlagsEnum;
                        }
                        return BindableType.Enum;
                    }
                    else if (typeSymbol is INamedTypeSymbol symbol && symbol.TypeArguments.Length == 1 && symbol.TypeArguments[0].TypeKind == TypeKind.Enum)
                    {
                        return BindableType.ListEnum;
                    }
                    return BindableType.None;
            }
        }

        #endregion

        #region 命名空间生成

        private bool StartNamespace(TypeInfo typeInfo, StringBuilder builder)
        {
            if (!string.IsNullOrEmpty(typeInfo.namedspace))
            {
                builder.Append("namespace ");
                builder.Append(typeInfo.namedspace);
                builder.AppendLine();
                builder.AppendLine("{");
                return true;
            }
            return false;
        }

        private void EndNamespace(bool haveNamespace, StringBuilder builder)
        {
            if (haveNamespace)
                builder.AppendLine("}");
        }
        #endregion

        #region 类型

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
                source.Append(BINDABLE_INTERFACE);
            }

            source.AppendLine();

            if (haveNamespace)
                source.Append('\t');
            source.AppendLine("{");
        }
        private void EndType(bool haveNamespace, StringBuilder builder)
        {
            if (haveNamespace)
                builder.Append('\t');
            builder.AppendLine("}");
        }
        #endregion

        #region 生成BindableInfo

        private void GenerateBindableInfo(bool haveNamespace, TypeInfo typeInfo, List<PropertyInfo> propertyInfos, StringBuilder source)
        {
            source.Append(haveNamespace ? "\t\t" : "\t");
            source.AppendLine("#region BindableInfo");
            source.Append(haveNamespace ? "\t\t" : "\t");
            source.Append("private static readonly UniVue.Model.BindableTypeInfo __typeInfo = new UniVue.Model.BindableTypeInfo");
            source.Append('(');
            source.Append('\"');
            //  public BindableTypeInfo(string typeName, BindablePropertyInfo[] properties)
            source.Append(typeInfo.typeName);
            source.Append('\"');
            source.Append(", ");
            source.Append('\"');
            source.Append(typeInfo.typeFullName);
            source.Append('\"');
            source.Append(", ");
            source.Append("new UniVue.Model.BindablePropertyInfo[]");
            source.Append('{');
            for (int i = 0; i < propertyInfos.Count; i++)
            {
                PropertyInfo propertyInfo = propertyInfos[i];

                if (propertyInfo.isInherit && propertyInfos.Exists(p => !p.isInherit && p.propertyName == propertyInfo.propertyName))
                {
                    continue;
                }

                source.Append("new UniVue.Model.BindablePropertyInfo");
                source.Append('(');
                //public BindablePropertyInfo(BindableType bindType, string propertyName, string typeFullName)
                source.Append("UniVue.Model.BindableType.");
                source.Append(propertyInfo.bindType.ToString());
                source.Append(", ");
                source.Append('\"');
                source.Append(propertyInfo.propertyName);
                source.Append('\"');
                source.Append(", ");
                source.Append('\"');
                source.Append(propertyInfo.typeFullName);
                source.Append('\"');
                source.Append(')');
                if (i != propertyInfos.Count - 1)
                    source.Append(", ");
            }
            source.Append('}');
            source.AppendLine(");");

            source.Append(haveNamespace ? "\t\t" : "\t");
            source.AppendLine("#endregion");

            source.Append(haveNamespace ? "\t\t" : "\t");
            if (typeInfo.isSealed && !typeInfo.baseHadImplInterface)
                source.AppendLine("public UniVue.Model.BindableTypeInfo TypeInfo => __typeInfo;\n");
            else if (!typeInfo.baseHadImplInterface && !typeInfo.isSealed)
                source.AppendLine("public virtual UniVue.Model.BindableTypeInfo TypeInfo => __typeInfo;\n");
            else if (typeInfo.baseHadImplInterface)
                source.AppendLine("public override UniVue.Model.BindableTypeInfo TypeInfo => __typeInfo;\n");
        }

        #endregion

        #region 事件

        private bool AddEvent(bool haveNamespace, TypeInfo typeInfo, StringBuilder source)
        {
            if (typeInfo.useEvent)
            {
                source.Append(haveNamespace ? "\t\t" : "\t");
                source.AppendLine("/// <summary>");
                source.Append(haveNamespace ? "\t\t" : "\t");
                source.AppendLine("/// 当属性值发生改变时调用");
                source.Append(haveNamespace ? "\t\t" : "\t");
                source.AppendLine("/// <para>参数0-属性名  参数1-当前对象  参数2-旧值</para>");
                source.Append(haveNamespace ? "\t\t" : "\t");
                source.AppendLine("/// </summary>");
                source.Append(haveNamespace ? "\t\t" : "\t");
                source.AppendLine($"public {(typeInfo.baseUseEvent ? "new" : "")} event System.Action<string, {typeInfo.typeFullName}, object> OnPropertyChanged;\n");
                return true;
            }
            return false;
        }

        #endregion

        #region 静态构造函数中注册EnumInfo

        private void AddEnumInfo(TypeInfo typeInfo, StringBuilder source)
        {
            List<PropertyInfo> properties = typeInfo.properties.Where(p =>
             (p.bindType == BindableType.Enum || p.bindType == BindableType.FlagsEnum) &&
             (_enums.ContainsKey(p.typeFullName)))?.ToList();

            if (properties != null && properties.Count > 0)
            {
                for (int i = 0; i < properties.Count; i++)
                {
                    if (_enumGenerated.TryGetValue(properties[i].typeFullName, out bool flag) && flag)
                    {
                        properties.RemoveAt(i--);
                    }
                }
                if (properties.Count <= 0) return;

                source.Append(typeInfo.namedspace == null ? "\t" : "\t\t");
                source.Append("static ");
                source.Append(typeInfo.typeName);
                source.AppendLine("()");
                source.Append(typeInfo.namedspace == null ? "\t" : "\t\t");
                source.AppendLine("{");

                for (int k = 0; k < properties.Count; k++)
                {
                    PropertyInfo property = properties[k];
                    if (property.bindType == BindableType.Enum || property.bindType == BindableType.FlagsEnum)
                    {
                        source.Append(typeInfo.namedspace == null ? "\t\t" : "\t\t\t");
                        source.Append("UniVue.ViewModel.EnumInfo ");
                        source.Append("enum");
                        source.Append(k);
                        source.Append(" = new UniVue.ViewModel.EnumInfo");
                        source.Append('(');
                        source.Append('\"');
                        source.Append(property.typeFullName);
                        source.Append('\"');
                        source.Append(", ");
                        var fields = _enums[property.typeFullName].GetMembers().OfType<IFieldSymbol>();
                        _enumGenerated.Add(property.typeFullName, true); //每添加一次就标识为已经添加过
                        source.Append("new UniVue.ViewModel.EnumValueInfo");
                        source.Append('[');
                        source.Append(fields.Count());
                        source.Append(']');
                        source.Append('{');

                        List<(Language, string)> aliasInfo = new List<(Language, string)>();
                        foreach (var field in fields)
                        {
                            // public EnumValueInfo(int intValue, string stringValue, object enumValue, AliasInfo[] aliases)
                            source.Append("new UniVue.ViewModel.EnumValueInfo");
                            source.Append('(');
                            // public EnumValueInfo(int intValue, string stringValue, AliasInfo[] aliases)
                            source.Append((int)field.ConstantValue);
                            source.Append(", ");
                            source.Append('\"');
                            source.Append(field.Name);
                            source.Append('\"');
                            source.Append(", ");
                            source.Append(field.ToDisplayString());
                            source.Append(", ");
                            var attrs = field.GetAttributes();
                            if (attrs != null && attrs.Length > 0)
                            {
                                foreach (var attr in attrs)
                                {
                                    if (attr.AttributeClass != null && attr.AttributeClass.ToDisplayString() == "UniVue.ViewModel.EnumAliasAttribute")
                                    {
                                        if (attr.ConstructorArguments.Length == 1)
                                            aliasInfo.Add((Language.None, attr.ConstructorArguments[0].Value.ToString()));
                                        else if (attr.ConstructorArguments.Length == 2)
                                            aliasInfo.Add(((Language)(int)attr.ConstructorArguments[0].Value, attr.ConstructorArguments[1].Value.ToString()));
                                    }
                                }
                                if (aliasInfo.Count > 0)
                                {
                                    source.Append("new UniVue.ViewModel.AliasInfo");
                                    source.Append('[');
                                    source.Append(aliasInfo.Count);
                                    source.Append(']');
                                    source.Append('{');
                                    for (int i = 0; i < aliasInfo.Count; i++)
                                    {
                                        var info = aliasInfo[i];
                                        source.Append("new UniVue.ViewModel.AliasInfo");
                                        source.Append('(');
                                        source.Append("UniVue.i18n.Language.");
                                        source.Append(info.Item1.ToString());
                                        source.Append(", ");
                                        source.Append('\"');
                                        source.Append(info.Item2);
                                        source.Append('\"');
                                        source.Append(')');
                                        if (i != aliasInfo.Count - 1)
                                        {
                                            source.Append(", ");
                                        }
                                    }
                                    source.Append('}');
                                }
                            }
                            else
                            {
                                source.Append("null");
                            }
                            source.Append(')');
                            source.Append(',');
                            aliasInfo.Clear();
                        }
                        source.Remove(source.Length - 1, 1);
                        source.Append('}');
                        source.AppendLine(");");
                        source.Append(typeInfo.namedspace == null ? "\t\t" : "\t\t\t");
                        source.Append("UniVue.ViewModel.Enums.AddEnumInfo(enum");
                        source.Append(k);
                        source.AppendLine(");");
                    }
                }
                source.Append(typeInfo.namedspace == null ? "\t" : "\t\t");
                source.AppendLine("}");
            }
        }

        #endregion

        #region 属性

        private bool IsNewProperty(PropertyInfo propertyInfo, List<PropertyInfo> propertyInfos)
        {
            return propertyInfos.Exists(p => p.isInherit && p.typeFullName == propertyInfo.typeFullName);
        }

        private void AddProperty(bool haveNamespace, bool generateEvent, PropertyInfo propertyInfo, StringBuilder source, List<PropertyInfo> propertyInfos)
        {
            //写入注释内容
            if (!string.IsNullOrEmpty(propertyInfo.comment))
                source.Append(propertyInfo.comment);

            source.Append(haveNamespace ? "\t\t" : "\t");
            source.Append("public ");
            if (IsNewProperty(propertyInfo, propertyInfos))
            {
                source.Append("new ");
            }
            source.Append(propertyInfo.typeFullName);
            source.Append(' ');
            source.AppendLine(propertyInfo.propertyName);
            source.Append(haveNamespace ? "\t\t" : "\t");
            source.AppendLine("{");

            //获取所有get要注入的代码
            List<CodeInjectInfo> codesInGet = propertyInfo.codes?.FindAll(c => c.type == InjectType.Get);

            if (codesInGet != null)
            {
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("get");
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("{");

                foreach (var code in codesInGet)
                {
                    foreach (var codeLine in code.codes)
                    {
                        if (string.IsNullOrWhiteSpace(codeLine)) continue;
                        source.Append(haveNamespace ? "\t\t\t\t" : "\t\t\t");
                        source.Append(codeLine);
                        source.Append(";\n");
                    }
                }

                source.Append(haveNamespace ? "\t\t\t\t" : "\t\t\t");
                source.Append("return this.");
                source.Append(propertyInfo.fieldName);
                source.AppendLine(";");

                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("}");
            }
            else
            {
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.Append("get => this.");
                source.Append(propertyInfo.fieldName);
                source.AppendLine(";");
            }

            source.Append(haveNamespace ? "\t\t\t" : "\t\t");
            source.AppendLine("set");
            source.Append(haveNamespace ? "\t\t\t" : "\t\t");
            source.AppendLine("{");
            source.Append(haveNamespace ? "\t\t\t\t" : "\t\t\t");
            source.Append("if (this.");
            source.Append(propertyInfo.fieldName);
            source.AppendLine(" != value)");
            source.Append(haveNamespace ? "\t\t\t\t" : "\t\t\t");
            source.AppendLine("{");

            List<CodeInjectInfo> codesInSetBeforChenged = propertyInfo.codes?.FindAll(c => c.type == InjectType.Set_BeforeChanged);
            if (codesInSetBeforChenged != null)
            {
                foreach (var code in codesInSetBeforChenged)
                {
                    foreach (var codeLine in code.codes)
                    {
                        if (string.IsNullOrWhiteSpace(codeLine)) continue;
                        source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                        source.Append(codeLine);
                        source.Append(";\n");
                    }
                }
            }

            source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
            source.Append("UniVue.Vue.Updater.UpdateUI(this, \"");
            source.Append(propertyInfo.propertyName);
            source.Append('"');
            if (propertyInfo.isEnumType)
                source.AppendLine(", (int)value);");
            else
                source.AppendLine(", value);");

            if (generateEvent)
            {
                source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                source.Append("object oldValue = this.");
                source.Append(propertyInfo.fieldName);
                source.AppendLine(";");

                //记录下被关联的属性在修改前的值
                if (propertyInfo.alsoNotifyOther != null)
                {
                    for (int i = 0; i < propertyInfo.alsoNotifyOther.Count; i++)
                    {
                        string propertyName = propertyInfo.alsoNotifyOther[i];
                        source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                        source.Append("object oldValue_");
                        source.Append(propertyName);
                        source.Append(" = this.");
                        source.Append(propertyName);
                        source.AppendLine(";");
                    }
                }

                source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                source.Append("this.");
                source.Append(propertyInfo.fieldName);
                source.AppendLine(" = value;");
                source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                source.AppendLine($"OnPropertyChanged?.Invoke(\"{propertyInfo.propertyName}\", this, oldValue);");
            }
            else
            {
                source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                source.Append("this.");
                source.Append(propertyInfo.fieldName);
                source.AppendLine(" = value;");
            }

            List<CodeInjectInfo> codesInSetAfterChenged = propertyInfo.codes?.FindAll(c => c.type == InjectType.Set_AfterChanged);
            if (codesInSetAfterChenged != null)
            {
                foreach (var code in codesInSetAfterChenged)
                {
                    foreach (var codeLine in code.codes)
                    {
                        if (string.IsNullOrWhiteSpace(codeLine)) continue;
                        source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                        source.Append(codeLine);
                        source.Append(";\n");
                    }
                }
            }

            if (propertyInfo.alsoNotifyOther != null)
            {
                for (int i = 0; i < propertyInfo.alsoNotifyOther.Count; i++)
                {
                    string propertyName = propertyInfo.alsoNotifyOther[i];
                    source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                    source.Append("UniVue.Vue.Updater.UpdateUI(this, \"");
                    source.Append(propertyInfo.alsoNotifyOther[i]);
                    source.Append('"');
                    PropertyInfo also = propertyInfos.Find(p => p.propertyName == propertyName);
                    if (also.isEnumType)
                        source.Append(", (int)this.");
                    else
                        source.Append(", this.");
                    source.Append(also.propertyName);
                    source.AppendLine(");");

                    if (generateEvent)
                    {
                        source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                        source.AppendLine($"if (!Equals(this.{propertyName}, oldValue_{propertyName}))");
                        source.Append(haveNamespace ? "\t\t\t\t\t\t" : "\t\t\t\t\t");
                        source.AppendLine($"OnPropertyChanged?.Invoke(\"{propertyName}\", this, oldValue_{propertyName});");
                    }
                }
            }
            source.Append(haveNamespace ? "\t\t\t\t" : "\t\t\t");
            source.AppendLine("}"); //end if
            source.Append(haveNamespace ? "\t\t\t" : "\t\t");
            source.AppendLine("}"); // end set
            source.Append(haveNamespace ? "\t\t" : "\t");
            source.AppendLine("}\n"); // end property
        }


        #endregion

        #region 方法

        /*
        private void AddRenderViewMethod(bool haveNamespace, TypeInfo typeInfo, List<PropertyInfo> propertyInfos, StringBuilder source)
        {
            source.Append(haveNamespace ? "\t\t" : "\t");
            if (!typeInfo.isSealed && !typeInfo.baseHadImplInterface)
                source.AppendLine("public virtual void RenderView()");
            else if (!typeInfo.baseHadImplInterface && typeInfo.isSealed)
                source.AppendLine("public void RenderView()");
            else if (typeInfo.baseHadImplInterface)
                source.AppendLine("public override void RenderView()");

            source.Append(haveNamespace ? "\t\t{\n" : "\t{\n");
            if (typeInfo.properties.Length > 0)
            {
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("UniVue.ViewModel.ViewUpdater updater = UniVue.Vue.Updater;");
                for (int i = 0; i < typeInfo.properties.Length; i++)
                {
                    PropertyInfo propertyInfo = typeInfo.properties[i];
                    source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                    source.Append("updater.UpdateUI(this, ");
                    source.Append('"');
                    source.Append(propertyInfo.propertyName);
                    source.Append('"');
                    if (propertyInfo.isEnumType)
                        source.Append(", (int)this.");
                    else
                        source.Append(", this.");
                    source.Append(propertyInfo.fieldName);
                    source.AppendLine(");");
                }
            }
            source.Append(haveNamespace ? "\t\t}\n\n" : "\t}\n\n");
        }
        */

        private void AddUpdateModelMethod(bool haveNamespace, TypeInfo typeInfo, List<PropertyInfo> propertyInfos, StringBuilder source)
        {
            if (propertyInfos.Count == 0) { return; }

            // 获取 int 和 枚举 类型的字段集合
            List<PropertyInfo> intAndEnumFields = typeInfo.properties.Where(f => f.specialType == SpecialType.System_Int32 || f.kind == TypeKind.Enum).ToList();
            // 获取 string 类型的字段集合
            List<PropertyInfo> stringFields = typeInfo.properties.Where(f => f.specialType == SpecialType.System_String).ToList();
            // 获取 float 类型的字段集合
            List<PropertyInfo> floatFields = typeInfo.properties.Where(f => f.specialType == SpecialType.System_Single).ToList();
            // 获取 bool 类型的字段集合
            List<PropertyInfo> boolFields = typeInfo.properties.Where(f => f.specialType == SpecialType.System_Boolean).ToList();

            _AddUpdateModelMethod(haveNamespace, typeInfo, "int", intAndEnumFields, source);
            _AddUpdateModelMethod(haveNamespace, typeInfo, "float", floatFields, source);
            _AddUpdateModelMethod(haveNamespace, typeInfo, "string", stringFields, source);
            _AddUpdateModelMethod(haveNamespace, typeInfo, "bool", boolFields, source);
        }

        private void _AddUpdateModelMethod(bool haveNamespace, TypeInfo typeInfo, string typeStr, List<PropertyInfo> propertyInfos, StringBuilder source)
        {
            source.Append(haveNamespace ? "\t\t" : "\t");
            if (typeInfo.isSealed && !typeInfo.baseHadImplInterface)
                source.AppendLine($"public void UpdateModel(string propertyName, {typeStr} propertyValue)");
            else if (!typeInfo.baseHadImplInterface && !typeInfo.isSealed)
                source.AppendLine($"public virtual void UpdateModel(string propertyName, {typeStr} propertyValue)");
            else if (typeInfo.baseHadImplInterface)
                source.AppendLine($"public override void UpdateModel(string propertyName, {typeStr} propertyValue)");
            source.Append(haveNamespace ? "\t\t" : "\t");
            source.AppendLine("{");

            if (typeInfo.baseHadImplInterface)
            {
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("base.UpdateModel(propertyName, propertyValue);");
            }

            //多于2个采用switch语句
            if (propertyInfos.Count >= 3)
            {
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("switch(propertyName)");
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("{");
                foreach (var property in propertyInfos)
                {
                    source.Append(haveNamespace ? "\t\t\t\t" : "\t\t\t");
                    source.AppendLine($"case \"{property.propertyName}\":");
                    source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                    if (property.isEnumType)
                        source.AppendLine($"this.{property.propertyName} = ({property.typeFullName})propertyValue;");
                    else
                        source.AppendLine($"this.{property.propertyName} = propertyValue;");
                    source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                    source.AppendLine("break;");
                }
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("}");
            }
            //采用if语句
            else
            {
                for (int i = 0; i < propertyInfos.Count; i++)
                {
                    PropertyInfo property = propertyInfos[i];
                    source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                    if (i == 0)
                        source.AppendLine($"if(\"{property.propertyName}\".Equals(propertyName))");
                    else
                        source.AppendLine($"else if(\"{property.propertyName}\".Equals(propertyName))");

                    source.Append(haveNamespace ? "\t\t\t\t" : "\t\t\t");
                    if (property.isEnumType)
                        source.AppendLine($"this.{property.propertyName} = ({property.typeFullName})propertyValue;");
                    else
                        source.AppendLine($"this.{property.propertyName} = propertyValue;");
                }
            }

            source.Append(haveNamespace ? "\t\t}\n" : "\t}\n");
        }

        private void AddConsumeableModelMethod(bool haveNamespace, TypeInfo typeInfo, StringBuilder source)
        {
            source.Append(haveNamespace ? "\t\t" : "\t");
            if (typeInfo.isSealed && !typeInfo.baseHadImplInterface)
                source.AppendLine("public void UpdateUI(string propertyName, UniVue.ViewModel.ModelUI modelUI)");
            else if (!typeInfo.isSealed && !typeInfo.baseHadImplInterface)
                source.AppendLine("public virtual void UpdateUI(string propertyName, UniVue.ViewModel.ModelUI modelUI)");
            else if (typeInfo.baseHadImplInterface)
                source.AppendLine("public override void UpdateUI(string propertyName, UniVue.ViewModel.ModelUI modelUI)");
            source.Append(haveNamespace ? "\t\t" : "\t");
            source.Append("{\n");

            if (typeInfo.baseHadImplInterface)
            {
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("base.UpdateUI(propertyName, modelUI);");
            }

            //多于2个采用switch语句
            if (typeInfo.properties.Length >= 3)
            {
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("switch(propertyName)");
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("{");
                foreach (var property in typeInfo.properties)
                {
                    source.Append(haveNamespace ? "\t\t\t\t" : "\t\t\t");
                    source.AppendLine($"case \"{property.propertyName}\":");
                    source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                    if (property.isEnumType)
                        source.AppendLine($"modelUI.UpdateUI(\"{property.propertyName}\", (int)this.{property.fieldName});");
                    else
                        source.AppendLine($"modelUI.UpdateUI(\"{property.propertyName}\", this.{property.fieldName});");
                    source.Append(haveNamespace ? "\t\t\t\t\t" : "\t\t\t\t");
                    source.AppendLine("break;");
                }
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("}");
            }
            //采用if语句
            else
            {
                for (int i = 0; i < typeInfo.properties.Length; i++)
                {
                    source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                    PropertyInfo propertyInfo = typeInfo.properties[i];
                    if (i == 0)
                        source.AppendLine($"if(nameof({propertyInfo.propertyName}).Equals(propertyName))");
                    else
                        source.AppendLine($"else if(nameof({propertyInfo.propertyName}).Equals(propertyName))");

                    source.Append(haveNamespace ? "\t\t\t\t" : "\t\t\t");
                    if (propertyInfo.isEnumType)
                        source.AppendLine($"modelUI.UpdateUI(nameof({propertyInfo.propertyName}), (int)this.{propertyInfo.fieldName});");
                    else
                        source.AppendLine($"modelUI.UpdateUI(nameof({propertyInfo.propertyName}), this.{propertyInfo.fieldName});");
                }
            }

            source.Append(haveNamespace ? "\t\t" : "\t");
            source.AppendLine("}\n");
        }

        private void AddConsumeableModelAllMethod(bool haveNamespace, TypeInfo typeInfo, StringBuilder source)
        {
            source.Append(haveNamespace ? "\t\t" : "\t");
            if (typeInfo.isSealed && !typeInfo.baseHadImplInterface)
                source.AppendLine("public void UpdateAll(UniVue.ViewModel.ModelUI modelUI)");
            else if (!typeInfo.isSealed && !typeInfo.baseHadImplInterface)
                source.AppendLine("public virtual void UpdateAll(UniVue.ViewModel.ModelUI modelUI)");
            else if (typeInfo.baseHadImplInterface)
                source.AppendLine("public override void UpdateAll(UniVue.ViewModel.ModelUI modelUI)");

            source.Append(haveNamespace ? "\t\t" : "\t");
            source.Append("{\n");

            if (typeInfo.baseHadImplInterface)
            {
                source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                source.AppendLine("base.UpdateAll(modelUI);");
            }

            if (typeInfo.properties.Length > 0)
            {
                foreach (var property in typeInfo.properties)
                {
                    source.Append(haveNamespace ? "\t\t\t" : "\t\t");
                    if (property.isEnumType)
                        source.AppendLine($"modelUI.UpdateUI(\"{property.propertyName}\", (int)this.{property.fieldName});");
                    else
                        source.AppendLine($"modelUI.UpdateUI(\"{property.propertyName}\", this.{property.fieldName});");
                }
            }
            source.Append(haveNamespace ? "\t\t" : "\t");
            source.Append("}\n\n");
        }


        #endregion

    }


}
