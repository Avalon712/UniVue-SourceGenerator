using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UniVue.SourceGenerator
{
    [Generator]
    public class AutoNotifyGenerator : ISourceGenerator
    {
        private const string AUTO_NOTIFY_ATTRIBUTE = "UniVue.Model.AutoNotifyAttribute";
        private const string BINDER_INTERFACE = "UniVue.Model.IBindableModel";

        public void Initialize(GeneratorInitializationContext context)
        {
            // 注册一个语法接收器
            context.RegisterForSyntaxNotifications(() => new SyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            // 获取填充的接收器
            if (!(context.SyntaxContextReceiver is SyntaxReceiver receiver)) return;

            // 获取添加的属性，以及实现IModel接口
            INamedTypeSymbol attributeSymbol = context.Compilation.GetTypeByMetadataName(AUTO_NOTIFY_ATTRIBUTE);
            INamedTypeSymbol interfaceSymbol = context.Compilation.GetTypeByMetadataName(BINDER_INTERFACE);

            // 将字段按类分组，并生成源
            foreach (IGrouping<INamedTypeSymbol, IFieldSymbol> group in
                receiver.Fields.GroupBy<IFieldSymbol, INamedTypeSymbol>(f => f.ContainingType, SymbolEqualityComparer.Default))
            {
                string classSource = ProcessClass(group.Key, group.ToList(), attributeSymbol, interfaceSymbol, context);
                if (classSource != null)
                {
                    context.AddSource($"{group.Key.Name}.g.cs", SourceText.From(classSource, Encoding.UTF8));
                }
            }
        }

        private bool IsNamespace(string namespaceName)
        {
            string pattern = @"^([A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*$";
            return !string.IsNullOrEmpty(namespaceName) && Regex.IsMatch(namespaceName, pattern);
        }


        private string ProcessClass(INamedTypeSymbol classSymbol, List<IFieldSymbol> fields, ISymbol attributeSymbol, INamedTypeSymbol notifySymbol, GeneratorExecutionContext context)
        {
            //必须是一个顶级的
            if (!classSymbol.ContainingSymbol.Equals(classSymbol.ContainingNamespace, SymbolEqualityComparer.Default))
            {
                // 创建一个简单的诊断
                var diagnostic = Diagnostic.Create(
                    "AutoNotifyGenerator001",
                    "SourceGenerator.AutoNotifyGenerator",
                    $"{classSymbol.ToDisplayString()}: 使用[AutoNotify]特性的类或结构体必须是一个顶级的，即它不能是一个嵌套的类或结构体",
                    DiagnosticSeverity.Warning,
                    DiagnosticSeverity.Warning,
                    isEnabledByDefault: true,
                    warningLevel: 1
                );
                // 使用 ReportDiagnostic 方法发送诊断
                context.ReportDiagnostic(diagnostic);
                return null;
            }

            ////必须声明为public和partial
            if (classSymbol.DeclaredAccessibility != Accessibility.Public && classSymbol.DeclaredAccessibility != Accessibility.Internal)
            {
                // 创建一个简单的诊断
                var diagnostic = Diagnostic.Create(
                    "AutoNotifyGenerator002",
                    "SourceGenerator.AutoNotifyGenerator",
                    $"{classSymbol.ToDisplayString()}: 使用[AutoNotify]特性的类必须是具有public或internal的访问修饰符以及是一个部分的partial类",
                    DiagnosticSeverity.Warning,
                    DiagnosticSeverity.Warning,
                    isEnabledByDefault: true,
                    warningLevel: 2
                );
                // 使用 ReportDiagnostic 方法发送诊断
                context.ReportDiagnostic(diagnostic);
                return null;
            }

            string namespaceName = classSymbol.ContainingNamespace.ToDisplayString();
            bool isNamespace = IsNamespace(namespaceName);

            //开始构建生成的源代码
            StringBuilder source = new StringBuilder();

            if (isNamespace)
            {
                source.Append("namespace ");
                source.Append(namespaceName);
                source.Append("\n{\n");
            }

            string accessStr = classSymbol.DeclaredAccessibility.ToString().ToLower();
            source.Append('\t');
            source.Append(accessStr);
            source.Append(" partial "); 
            source.Append(classSymbol.IsValueType ? "struct " : "class ");
            source.Append(classSymbol.Name);

            // 如果这个类或结构体还没有实现IBindableModel接口，添加它
            if (!classSymbol.Interfaces.Contains(notifySymbol, SymbolEqualityComparer.Default))
            {
                source.Append($" : {notifySymbol.ToDisplayString()}");
            }

            source.Append("\n\t{\n");

            // 为每个字段创建属性
            foreach (IFieldSymbol fieldSymbol in fields)
            {
                GeneratePropertyMethod(notifySymbol, source, fieldSymbol, attributeSymbol);
            }

            //重写NotifyAll()和UpdateModel()这里基于反射实现的函数
            GenerateNotifyAllMethod(fields, attributeSymbol, source);
            GenerateUpdateModelMethod(fields, attributeSymbol, source);

            //实现IConsumableModel接口中的方法
            GenerateConsumeableModelMethod(fields, attributeSymbol, source);
            GenerateConsumeableModelAllMethod(fields, attributeSymbol, source);

            source.Append("\n\t}\n");

            if(isNamespace)
                source.Append("}");

            return source.ToString();
        }

        private void GeneratePropertyMethod(INamedTypeSymbol notifier, StringBuilder source, IFieldSymbol fieldSymbol, ISymbol attributeSymbol)
        {
            var property = GetPropertyPrefix(fieldSymbol, attributeSymbol);

            if (property.Item2 == null) { return; }

            string enumCastStr = fieldSymbol.Type.BaseType.SpecialType == SpecialType.System_Enum ? "(int)" : string.Empty;

            source.Append($@"
        public {property.Item1} {property.Item2} 
        {{
            get => this.{fieldSymbol.Name};

            set
            {{
                if(this.{fieldSymbol.Name} != value)
                {{
                    (({notifier.ToDisplayString()})this).NotifyUIUpdate(nameof({property.Item2}), {enumCastStr}value);
                    this.{fieldSymbol.Name} = value;
                }}
            }}
        }}
");
        }

     
        private void GenerateNotifyAllMethod(List<IFieldSymbol> fields, ISymbol attributeSymbol, StringBuilder source)
        {
            source.Append("\t\tvoid UniVue.Model.IUINotifier.NotifyAll()\n");
            source.Append("\t\t{\n");
           
            if(fields.Count > 0)
            {
                source.AppendLine($"\t\t\t{BINDER_INTERFACE} model = this;");
                foreach (var field in fields)
                {
                    var property = GetPropertyPrefix(field, attributeSymbol);
                    if (property.Item2 == null) { continue; }
                    string enumCastStr = field.Type.BaseType.SpecialType == SpecialType.System_Enum ? "(int)" : string.Empty;
                    source.AppendLine($"\t\t\tmodel.NotifyUIUpdate(nameof({property.Item2}), {enumCastStr}{property.Item2});");
                }
            }
            
            source.Append("\t\t}\n");
        }

        private void GenerateUpdateModelMethod(List<IFieldSymbol> fields, ISymbol attributeSymbol, StringBuilder source)
        {
            if (fields.Count == 0) { return; }

            // 获取 int 和 枚举 类型的字段集合
            var intAndEnumFields = fields.Where(f => f.Type.SpecialType == SpecialType.System_Int32 || f.Type.TypeKind == TypeKind.Enum).ToList();
            // 获取 string 类型的字段集合
            var stringFields = fields.Where(f => f.Type.SpecialType == SpecialType.System_String).ToList();
            // 获取 float 类型的字段集合
            var floatFields = fields.Where(f => f.Type.SpecialType == SpecialType.System_Single).ToList();
            // 获取 bool 类型的字段集合
            var boolFields = fields.Where(f => f.Type.SpecialType == SpecialType.System_Boolean).ToList();

            GenerateUpdateModelMethod("int", intAndEnumFields, attributeSymbol, source);
            GenerateUpdateModelMethod("float", floatFields, attributeSymbol, source);
            GenerateUpdateModelMethod("string", stringFields, attributeSymbol, source);
            GenerateUpdateModelMethod("bool", boolFields, attributeSymbol, source);
        }

        private void GenerateUpdateModelMethod(string typeStr, List<IFieldSymbol> fields, ISymbol attributeSymbol, StringBuilder source)
        {
            source.AppendLine($"\t\tvoid UniVue.Model.IModelUpdater.UpdateModel(string propertyName, {typeStr} propertyValue)");
            source.AppendLine("\t\t{");

            //多于2个采用switch语句
            if (fields.Count >= 3)
            {
                source.AppendLine("\t\t\tswitch(propertyName)");
                source.AppendLine("\t\t\t{");
                foreach (var f in fields)
                {
                    var p = GetPropertyPrefix(f, attributeSymbol);
                    if (p.Item2 == null) { continue; }
                    source.AppendLine($"\t\t\t\tcase nameof({p.Item2}):");
                    if (f.Type.BaseType.SpecialType == SpecialType.System_Enum)
                        source.AppendLine($"\t\t\t\t\tthis.{p.Item2} = ({p.Item1})propertyValue;");
                    else
                        source.AppendLine($"\t\t\t\t\tthis.{p.Item2} = propertyValue;");
                    source.AppendLine("\t\t\t\t\t\tbreak;");
                }
                source.AppendLine("\t\t\t}");
            }
            //采用if语句
            else
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    var p = GetPropertyPrefix(fields[i], attributeSymbol);
                    if (p.Item2 == null) continue;
                    if (i == 0)
                        source.AppendLine($"\t\t\tif(nameof({p.Item2}).Equals(propertyName))");
                    else
                        source.AppendLine($"\t\t\telse if(nameof({p.Item2}).Equals(propertyName))");

                    if (fields[i].Type.BaseType.SpecialType == SpecialType.System_Enum)
                        source.AppendLine($"\t\t\t\tthis.{p.Item2} = ({p.Item1})propertyValue;");
                    else
                        source.AppendLine($"\t\t\t\tthis.{p.Item2} = propertyValue;");
                }
            }

            source.AppendLine("\t\t}\n");
        }

        private void GenerateConsumeableModelAllMethod(List<IFieldSymbol> fields, ISymbol attributeSymbol, StringBuilder source)
        {
            source.Append("\t\tvoid UniVue.Model.IConsumableModel.UpdateAll(UniVue.ViewModel.UIBundle bundle)\n");
            source.Append("\t\t{\n");

            if (fields.Count > 0)
            {
                foreach (var field in fields)
                {
                    var property = GetPropertyPrefix(field, attributeSymbol);
                    if (property.Item2 == null) { continue; }
                    string enumCastStr = field.Type.BaseType.SpecialType == SpecialType.System_Enum ? "(int)" : string.Empty;
                    source.AppendLine($"\t\t\tbundle.UpdateUI(nameof({property.Item2}), {enumCastStr}{property.Item2});");
                }
            }

            source.Append("\t\t}\n");
        }

        private void GenerateConsumeableModelMethod(List<IFieldSymbol> fields, ISymbol attributeSymbol, StringBuilder source)
        {
            source.Append("\t\tvoid UniVue.Model.IConsumableModel.UpdateUI(string propertyName, UniVue.ViewModel.UIBundle bundle)\n");
            source.Append("\t\t{\n");

            //多于2个采用switch语句
            if (fields.Count >= 3)
            {
                source.AppendLine("\t\t\tswitch(propertyName)");
                source.AppendLine("\t\t\t{");
                foreach (var field in fields)
                {//bundle.UpdateUI(propertyName, 12);
                    var p = GetPropertyPrefix(field, attributeSymbol);
                    if (p.Item2 == null) { continue; }
                    source.AppendLine($"\t\t\t\tcase nameof({p.Item2}):");
                    if (field.Type.BaseType.SpecialType == SpecialType.System_Enum)
                        source.AppendLine($"\t\t\t\t\tbundle.UpdateUI(nameof({p.Item2}), (int){p.Item2});");
                    else
                        source.AppendLine($"\t\t\t\t\tbundle.UpdateUI(nameof({p.Item2}), {p.Item2});");
                    source.AppendLine("\t\t\t\t\t\tbreak;");
                }
                source.AppendLine("\t\t\t}");
            }
            //采用if语句
            else
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    var p = GetPropertyPrefix(fields[i], attributeSymbol);
                    if (p.Item2 == null) continue;
                    if (i == 0)
                        source.AppendLine($"\t\t\tif(nameof({p.Item2}).Equals(propertyName))");
                    else
                        source.AppendLine($"\t\t\telse if(nameof({p.Item2}).Equals(propertyName))");

                    if (fields[i].Type.BaseType.SpecialType == SpecialType.System_Enum)
                        source.AppendLine($"\t\t\t\t\tbundle.UpdateUI(nameof({p.Item2}), (int){p.Item2});");
                    else
                        source.AppendLine($"\t\t\t\t\tbundle.UpdateUI(nameof({p.Item2}), {p.Item2});");
                }
            }

            source.Append("\t\t}\n");
        }

        private string FieldName2PropertyName(string fieldName, TypedConstant overridenNameOpt)
        {
            if (!overridenNameOpt.IsNull)
            {
                return overridenNameOpt.Value.ToString();
            }

            if(fieldName.StartsWith("_"))
                fieldName = fieldName.TrimStart('_');
            else if(fieldName.StartsWith("m_"))
                fieldName = fieldName.TrimStart('m', '_');

            if (fieldName.Length == 0)
                return string.Empty;

            if (fieldName.Length == 1)
                return fieldName.ToUpper();

            return fieldName.Substring(0, 1).ToUpper() + fieldName.Substring(1);
        }

        private ValueTuple<ITypeSymbol, string> GetPropertyPrefix(IFieldSymbol fieldSymbol, ISymbol attributeSymbol)
        {
            // 获取字段的名称和类型
            string fieldName = fieldSymbol.Name;
            ITypeSymbol fieldType = fieldSymbol.Type;

            // 从字段中获取automotify属性和任何相关数据
            AttributeData attributeData = fieldSymbol.GetAttributes().Single(ad => ad.AttributeClass.Equals(attributeSymbol, SymbolEqualityComparer.Default));
            TypedConstant overridenNameOpt = attributeData.NamedArguments.SingleOrDefault(kvp => kvp.Key == "PropertyName").Value;

            string propertyName = FieldName2PropertyName(fieldName, overridenNameOpt);
            if (propertyName.Length == 0 || propertyName == fieldName)
            {
                return default;
            }
            return (fieldType, propertyName);
        }


        #region 字段过滤

        /// <summary>
        /// Created on demand before each generation pass
        /// </summary>
        class SyntaxReceiver : ISyntaxContextReceiver
        {
            public List<IFieldSymbol> Fields { get; } = new List<IFieldSymbol>();

            /// <summary>
            /// Called for every syntax node in the compilation, we can inspect the nodes and save any information useful for generation
            /// </summary>
            public void OnVisitSyntaxNode(GeneratorSyntaxContext context)
            {
                // 任何具有至少一个属性的字段都可以生成属性
                if (context.Node is FieldDeclarationSyntax fieldSyntax
                    && fieldSyntax.AttributeLists.Count > 0)
                {
                    foreach (VariableDeclaratorSyntax variable in fieldSyntax.Declaration.Variables)
                    {
                        // 获取由字段声明的符号，如果是带注释的，则保留该符号
                        IFieldSymbol fieldSymbol = context.SemanticModel.GetDeclaredSymbol(variable) as IFieldSymbol;
                        string symbol = fieldSymbol.Type.ToDisplayString();
                        bool support = IsSupportType(symbol);
                        if (!support && symbol.StartsWith("System.Collections.Generic.List<"))
                        {
                            string enumStr = symbol.Replace("System.Collections.Generic.List<", string.Empty);
                            enumStr = enumStr.Replace(">", string.Empty);
                            support = IsListEnum(context.SemanticModel.Compilation.GetTypeByMetadataName(enumStr), symbol);
                        }
                        if ((support || fieldSymbol.Type.BaseType.SpecialType == SpecialType.System_Enum) &&
                            fieldSymbol.GetAttributes().Any(ad => ad.AttributeClass.ToDisplayString() == AUTO_NOTIFY_ATTRIBUTE))
                        {
                            Fields.Add(fieldSymbol);
                        }
                    }
                }
            }

            private bool IsSupportType(string symbol)
            {
                switch (symbol)
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
                    default: return false;
                }
            }

            private bool IsListEnum(INamedTypeSymbol named, string symbol)
            {
                if (named == null) { return false; }
                if (named.BaseType.SpecialType == SpecialType.System_Enum) { return true; }
                return false;
            }

        }

        #endregion
    }
}
