Imports System.IO
Imports System.Reflection
Imports System.Text.Json

''' <summary>
''' 构建期烘焙的 ffmpeg 能力表（capabilities.json）。
'''
''' 设计要点：
'''   · 运行时<b>不做任何探测</b>，只读这份静态数据，因此启动开销与机器无关；
'''   · 索引（编码器名/类型/codec/像素格式）在启动时一次性建好，供下拉框与兼容性求值使用；
'''   · 单个编码器的完整 AVOptions 是<b>懒加载</b>的——只有用户真正选中它、或打开高级参数时才解析。
'''
''' 数据来源：tools/dump-capabilities.ps1，仅在「首次构建」和「升级内置 ffmpeg」时重跑。
''' </summary>
Public Class Capabilities

    Public Property FfmpegVersion As String = ""
    Public Property GeneratedAt As String = ""
    Public Property Encoders As New List(Of EncoderInfo)
    Public Property Formats As New List(Of FormatInfo)

    ''' <summary>持有整个 JSON DOM；懒加载的选项会从这里取。</summary>
    Private _doc As JsonDocument

    Private Const ResourceSuffix As String = "capabilities.json"

    ''' <summary>从程序集内嵌资源加载。找不到时抛出可诊断的异常，而不是静默降级。</summary>
    Public Shared Function LoadEmbedded() As Capabilities
        Dim asm = Assembly.GetExecutingAssembly()
        Dim name = asm.GetManifestResourceNames().
            FirstOrDefault(Function(n) n.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase))

        If name Is Nothing Then
            Throw New InvalidOperationException(
                $"未找到内嵌的 {ResourceSuffix}。请检查 .vbproj 的 EmbeddedResource 配置，以及是否已运行 tools/dump-capabilities.ps1。")
        End If

        Using s = asm.GetManifestResourceStream(name)
            Return Load(s)
        End Using
    End Function

    ''' <summary>从任意流加载（便于单元测试与未来支持外部能力包）。</summary>
    Public Shared Function Load(stream As Stream) As Capabilities
        ' 注意：JsonDocument 在整个生命周期内必须存活，懒加载的 JsonElement 依赖它。
        Dim doc = JsonDocument.Parse(stream)
        Dim caps As New Capabilities With {._doc = doc}

        Dim root = doc.RootElement

        Dim ff As JsonElement
        If root.TryGetProperty("ffmpeg", ff) Then
            Dim v As JsonElement
            If ff.TryGetProperty("version", v) Then caps.FfmpegVersion = v.GetString()
        End If

        Dim ga As JsonElement
        If root.TryGetProperty("generatedAt", ga) Then caps.GeneratedAt = ga.GetString()

        Dim encs As JsonElement
        If root.TryGetProperty("encoders", encs) Then
            For Each e In encs.EnumerateArray()
                caps.Encoders.Add(EncoderInfo.FromJson(e))
            Next
        End If

        Dim fmts As JsonElement
        If root.TryGetProperty("formats", fmts) Then
            For Each f In fmts.EnumerateArray()
                caps.Formats.Add(FormatInfo.FromJson(f))
            Next
        End If

        Return caps
    End Function

    Public Function FindEncoder(name As String) As EncoderInfo
        If String.IsNullOrEmpty(name) Then Return Nothing
        Return Encoders.FirstOrDefault(Function(e) String.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
    End Function

    ''' <summary>按类型筛选：V=视频 A=音频 S=字幕。</summary>
    Public Function EncodersOfKind(kind As String) As IEnumerable(Of EncoderInfo)
        Return Encoders.Where(Function(e) e.Kind = kind)
    End Function

    ''' <summary>能否封装成指定容器。</summary>
    Public Function CanMuxTo(formatName As String) As Boolean
        Dim f = Formats.FirstOrDefault(Function(x) String.Equals(x.Name, formatName, StringComparison.OrdinalIgnoreCase))
        Return f IsNot Nothing AndAlso f.CanMux
    End Function
End Class


''' <summary>一个编码器的索引信息。完整选项表按需懒加载。</summary>
Public Class EncoderInfo

    Public Property Name As String = ""
    ''' <summary>V=视频 A=音频 S=字幕</summary>
    Public Property Kind As String = ""
    Public Property Codec As String = ""
    Public Property Desc As String = ""
    Public Property PixelFormats As String() = Array.Empty(Of String)()

    Friend Property Element As JsonElement

    Private _options As List(Of OptionInfo)

    ''' <summary>该编码器支持的全部 AVOptions（首次访问时解析）。</summary>
    Public ReadOnly Property Options As List(Of OptionInfo)
        Get
            If _options Is Nothing Then
                _options = New List(Of OptionInfo)()
                Dim arr As JsonElement
                If Element.ValueKind = JsonValueKind.Object AndAlso Element.TryGetProperty("options", arr) Then
                    For Each o In arr.EnumerateArray()
                        _options.Add(OptionInfo.FromJson(o))
                    Next
                End If
            End If
            Return _options
        End Get
    End Property

    ''' <summary>某个 flag 是否被这个编码器支持——L1 知识层的过时检测依赖它。</summary>
    Public Function SupportsFlag(flag As String) As Boolean
        Return Options.Any(Function(o) String.Equals(o.Flag, flag, StringComparison.OrdinalIgnoreCase))
    End Function

    Public Shared Function FromJson(e As JsonElement) As EncoderInfo
        Dim info As New EncoderInfo With {.Element = e}

        Dim tmp As JsonElement
        If e.TryGetProperty("name", tmp) Then info.Name = tmp.GetString()
        If e.TryGetProperty("kind", tmp) Then info.Kind = tmp.GetString()
        If e.TryGetProperty("codec", tmp) Then info.Codec = tmp.GetString()
        If e.TryGetProperty("desc", tmp) Then info.Desc = tmp.GetString()

        If e.TryGetProperty("pixelFormats", tmp) AndAlso tmp.ValueKind = JsonValueKind.Array Then
            info.PixelFormats = tmp.EnumerateArray().Select(Function(x) x.GetString()).ToArray()
        End If

        Return info
    End Function
End Class


''' <summary>单个 ffmpeg 选项的元数据，全部来自编译期的 `ffmpeg -h encoder=X`。</summary>
Public Class OptionInfo

    ''' <summary>命令行写法，含前导短横线，例如 "-crf"。</summary>
    Public Property Flag As String = ""
    Public Property Name As String = ""
    ''' <summary>int / float / string / boolean / rational / image_size / duration ...</summary>
    Public Property Type As String = ""
    Public Property Range As String = ""
    ''' <summary>ffmpeg 报告的默认值（字符串，按需转换）。</summary>
    Public Property DefaultValue As String = ""
    Public Property Desc As String = ""
    Public Property EnumValues As List(Of EnumValueInfo)

    Public ReadOnly Property HasEnum As Boolean
        Get
            Return EnumValues IsNot Nothing AndAlso EnumValues.Count > 0
        End Get
    End Property

    Public Shared Function FromJson(o As JsonElement) As OptionInfo
        Dim info As New OptionInfo()

        Dim tmp As JsonElement
        If o.TryGetProperty("flag", tmp) Then info.Flag = tmp.GetString()
        If o.TryGetProperty("name", tmp) Then info.Name = tmp.GetString()
        If o.TryGetProperty("type", tmp) Then info.Type = tmp.GetString()
        If o.TryGetProperty("range", tmp) Then info.Range = tmp.GetString()
        If o.TryGetProperty("default", tmp) Then info.DefaultValue = tmp.GetString()
        If o.TryGetProperty("desc", tmp) Then info.Desc = tmp.GetString()

        If o.TryGetProperty("enum", tmp) AndAlso tmp.ValueKind = JsonValueKind.Array Then
            info.EnumValues = New List(Of EnumValueInfo)()
            For Each v In tmp.EnumerateArray()
                info.EnumValues.Add(EnumValueInfo.FromJson(v))
            Next
        End If

        Return info
    End Function
End Class

Public Class EnumValueInfo
    Public Property Value As String = ""
    Public Property Number As Long
    Public Property Desc As String = ""

    Public Shared Function FromJson(v As JsonElement) As EnumValueInfo
        Dim info As New EnumValueInfo()
        Dim tmp As JsonElement
        If v.TryGetProperty("value", tmp) Then info.Value = tmp.GetString()
        If v.TryGetProperty("num", tmp) Then info.Number = tmp.GetInt64()
        If v.TryGetProperty("desc", tmp) Then info.Desc = tmp.GetString()
        Return info
    End Function
End Class


''' <summary>一个容器格式。</summary>
Public Class FormatInfo
    Public Property Name As String = ""
    Public Property Desc As String = ""
    Public Property CanMux As Boolean
    Public Property CanDemux As Boolean

    Public Shared Function FromJson(f As JsonElement) As FormatInfo
        Dim info As New FormatInfo()
        Dim tmp As JsonElement
        If f.TryGetProperty("name", tmp) Then info.Name = tmp.GetString()
        If f.TryGetProperty("desc", tmp) Then info.Desc = tmp.GetString()
        If f.TryGetProperty("mux", tmp) Then info.CanMux = tmp.GetBoolean()
        If f.TryGetProperty("demux", tmp) Then info.CanDemux = tmp.GetBoolean()
        Return info
    End Function
End Class
