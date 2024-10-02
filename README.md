# UniVue源生成器——自动生成接口代码

## 原生成器的使用

**注：使用源生成器需要Unity编辑器的版本为2021以上**

图文教程：[UniVue更新日志：使用源生成器优化Model和ViewModel层的设计-CSDN博客](https://blog.csdn.net/m0_62135731/article/details/139525492?spm=1001.2014.3001.5501)

### 1.导入源生器的dll文件到Unity中

将仓库中dlls目录下的**UniVue.SourceGenerator.dll**程序集文件导入到Unity的**Assets**目录下的任意一个目录。

### 2.配置dll文件

导入dll文件后需要将进行以下步骤：

1. 在资产浏览器中，单击**UniVue.SourceGenerator.dll**文件以打开插件检查器窗口。
2. 转到**为插件选择平台**（英文：**Select platforms for plugin**）并**禁用任何平台**（英文：**Any Platform**）。
3. 转到**包含平台**（英文：**Include Platforms**）并禁用所有选项。
4. 转到资产标签，然后打开**资产标签**子菜单。（就是点击那个检查面板右下角的蓝色小图标）
5. 创建并分配一个名为 ***RoslynAnalyzer***的新标签。为此，请在“**资产标签**”子菜单的文本输入窗口中输入“***\*RoslynAnalyzer\****”。此标签必须完全匹配且区分大小写。为第一个分析器创建标签后，标签将显示在***\*“资产标签\****”子菜单中。您可以单击菜单中的标签名称以将其分配给其他分析器。
6. 点击Apply，等待编译完成。
7. 重启Visual Studio编辑器。

### 3.使用特性

在使用UniVue的特性时，需要注意以下两点：

1. 只能在一个非嵌套的类上使用；
2. 依赖类生成的特性，那么类必须有partial关键字修饰；

内置的特性的用法：

#### 3.1 事件

**EventRegisterAttribute**：任何类上添加此特性将会自动实现IEventRegister接口，实现此接口的类才允许注册事件回调；

**EventCallAttribute**：注解此特性的方法将会映射一个EventCall对象，同时此特性的类上必须有**EventRegisterAttribute**特性，否则将不起作用；

#### 3.2 模型

**AlsoNotifyAttribute**：注解在字段上，当前属性更改时也通知指定的其它属性；

**BindableAttribute**：注解此特性的类将自动实现IBindableModel接口，将会为这个类中所有字段自动生成属性方法；

**CodeInjectAttribute**：在属性方法的指定位置注入指定代码；

**DontNotifyAttribute**：指定不要为注解此特性的字段生成通知属性；

**PropertyNameAttribute**：为字段定义属性名称；

#### 3.3 枚举

**EnumAliasAttribute**：为一个枚举值定义其它别名（不需要partial关键字）；