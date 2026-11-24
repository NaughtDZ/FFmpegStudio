Imports System.IO
Imports System.Reflection
Imports System.Text.Json
Imports System.Text.Json.Serialization

''' <summary>
''' L2 组合层：模块库。每个模块 = 若干 ffmpeg flag 的组合，
''' <see cref="ModuleDefinition.Bindings"/> 声明「什么编码器下用什么 flag」。
'''
''' 新增一个编码器的支持，通常只需要在相关模块里加一行 binding，不用改代码。
''' </summary>
Public Class ModuleLibrary

    Public Property SchemaVersion As Integer
    Public Property Modules As New List(Of ModuleDefinition)()

    Private Shared ReadOnly JsonOpts As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .ReadCommentHandling = JsonCommentHandling.Skip,
        .AllowTrailingCommas = True
    }

    Public Shared Function LoadEmbedded() As ModuleLibrary
        Dim asm = Assembly.GetExecutingAssembly()
        Dim name = asm.GetManifestResourceNames().
            FirstOrDefault(Function(n) n.EndsWith("modules.json", StringComparison.OrdinalIgnoreCase))

        If name Is Nothing Then
            Throw New InvalidOperationException("未找到内嵌的 modules.json，请检查 .vbproj 的 EmbeddedResource 配置。")
        End If

        Using s = asm.GetManifestResourceStream(name)
            Return Load(s)
        End Using
    End Function

    Public Shared Function Load(stream As Stream) As ModuleLibrary
        Dim library = JsonSerializer.Deserialize(Of ModuleLibrary)(stream, JsonOpts)
        If library Is Nothing Then Throw New InvalidOperationException("modules.json 解析结果为空。")
        Return library
    End Function

    Public Function Find(id As String) As ModuleDefinition
        If String.IsNullOrEmpty(id) Then Return Nothing
        Return Modules.FirstOrDefault(Function(m) String.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
    End Function

    Public Function ByScope(scope As String) As IEnumerable(Of ModuleDefinition)
        Return Modules.Where(Function(m) String.Equals(m.Scope, scope, StringComparison.OrdinalIgnoreCase))
    End Function
End Class


''' <summary>一个参数模块的静态定义（来自 modules.json）。</summary>
Public Class ModuleDefinition

    Public Property Id As String = ""
    ''' <summary>video / audio / container</summary>
    Public Property Scope As String = ""
    Public Property Category As String = ""
    Public Property Title As String = ""
    Public Property Subtitle As String = ""
    Public Property Icon As String = ""
    ''' <summary>"filter" 表示产出的是滤镜串（视频走 -vf，音频走 -af），空表示普通 flag。</summary>
    Public Property Kind As String = ""
    ''' <summary>basic / advanced</summary>
    Public Property Level As String = ""
    Public Property Doc As String = ""
    ''' <summary>卡片上显示的摘要模板，例如 "CRF {crf}"。</summary>
    Public Property Summary As String = ""

    ' <summary>滤镜链里的推荐位置（小=靠前）。0 表示不参与滤镜排序。
    ' 注意：这与编码器无关——滤镜在编码之前执行，换编码器不影响滤镜顺序。</summary>
    Public Property Order As Integer

    Public Property Controls As New List(Of ControlDefinition)()
    Public Property Bindings As New List(Of BindingDefinition)()
    ''' <summary>与这些模块互斥（同时存在时本模块变为已停用）。</summary>
    Public Property Conflicts As New List(Of String)()
    ' 必须先有这些模块在场，本模块才有意义（如两遍编码需要目标码率）
    Public Property Requires As New List(Of String)()
    ''' <summary>这些编码器下整个模块不可用（如 copy 模式）。</summary>
    Public Property ExcludeEncoders As New List(Of String)()

    Public ReadOnly Property IsFilter As Boolean
        Get
            Return String.Equals(Kind, "filter", StringComparison.OrdinalIgnoreCase)
        End Get
    End Property

    Public Function CreateInstance() As ModuleInstance
        Return New ModuleInstance(Me)
    End Function
End Class


''' <summary>模块里的一个可编辑控件（数字/枚举/文本…）。</summary>
Public Class ControlDefinition

    Public Property Key As String = ""
    <JsonPropertyName("type")>
    Public Property ParamType As String = ""
    Public Property Label As String = ""
    Public Property Min As Double?
    Public Property Max As Double?
    <JsonPropertyName("default")>
    Public Property DefaultValue As String = ""
    Public Property Options As New List(Of EnumOption)()
    Public Property Presets As New List(Of PresetOption)()

    ''' <summary>把默认值投影成运行时字符串。</summary>
    Public Function DefaultText() As String
        Return If(DefaultValue, "")
    End Function
End Class

Public Class EnumOption
    Public Property Value As String = ""
    Public Property Label As String = ""
End Class

Public Class PresetOption
    Public Property Label As String = ""
    Public Property Value As String = ""
End Class


''' <summary>一条「条件 → flag 序列」的绑定规则。</summary>
Public Class BindingDefinition

    <JsonPropertyName("when")>
    Public Property Condition As ConditionDefinition

    ''' <summary>要输出的 flag 序列，值用 {key} 占位。</summary>
    Public Property Emit As New List(Of String)()

    ''' <summary>逻辑值 → 实际 flag 值 的映射，用于同一概念在不同编码器上取值不同（如 preset 的 p1..p7）。</summary>
    Public Property ValueMap As Dictionary(Of String, String)

    ''' <summary>当某个控件取到指定值时，整条 binding 不输出（如「保持原始帧率」）。</summary>
    Public Property SkipIf As Dictionary(Of String, String)

    ' <summary>只有当某个控件取到指定值时才输出——用于一个模块有多种形态（如旋转的六种方式）。</summary>
    Public Property RunIf As Dictionary(Of String, String)

    Public Sub New()
        Condition = New ConditionDefinition()
    End Sub
End Class


''' <summary>绑定条件。列出的各项之间是 AND 关系。</summary>
Public Class ConditionDefinition

    ''' <summary>适用的编码器；"*" 表示任意。</summary>
    Public Property Encoder As List(Of String)

    ''' <summary>适用的容器；为空表示不限。</summary>
    Public Property Container As List(Of String)

    ''' <summary>要求编码器自身提供该选项（用于检测 ffmpeg 升级后参数是否还在）。</summary>
    Public Property RequiresFlag As String = ""
End Class


''' <summary>模块的运行时实例：保存用户填的值。</summary>
Public Class ModuleInstance

    Public Property Definition As ModuleDefinition
    Public Property Values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Public Property IsEnabled As Boolean = True

    Public Sub New(def As ModuleDefinition)
        Definition = def
        ResetToDefaults()
    End Sub

    Public Sub ResetToDefaults()
        Values.Clear()
        For Each c In Definition.Controls
            Values(c.Key) = c.DefaultText()
        Next
    End Sub

    ''' <summary>卡片上显示的摘要，例如 "CRF 20"。</summary>
    Public Function SummaryText() As String
        Dim s = Definition.Summary
        For Each kv In Values
            s = s.Replace("{" & kv.Key & "}", kv.Value)
        Next
        Return s
    End Function
End Class
