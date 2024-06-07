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

### 3.使用AutoNotifyAttribute特性

之后在任何你想要进行绑定的模型的字段上添加**[AutoNotify]**特性。但是此特性不是随意的，它具有以下限制：

1. 只能注解在字段上；
2. 只能注解在顶级类或结构体（即这个类或结构体的外部不能包含其它类或结构体）；
3. 类或结构体的修饰符必须为public partial，这是因为源生成器的实现原理如此。

注意：为了防止源生成器为标记了AutoNotifyAttribute的字段生成的属性重名，你应该了解源生器将字段名转为属性名的逻辑：

```
_name  =>  Name
m_name =>  Name
_headIcon => HeadIcon
m_headIcon => HeadIcon
即：将以下划线'_'和字符串"m_"的进行删掉，首字母转为大写即可。
```

**此外注解了[AutoNotify]的特性的类或结构体将会自动实现IBindableModel接口（如果你没有主动实现的话），因此你无需显示实现这个接口。**