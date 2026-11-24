Imports System.IO
Imports System.Text.Json
Imports System.Text.Json.Serialization

''' <summary>方案里保存的一个模块实例。</summary>
Public Class PresetModule
    Public Property Id As String = ""
    Public Property Values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
End Class

''' <summary>一份可保存/加载的方案：编码器 + 容器 + 媒体模式 + 整条参数链的取值。</summary>
Public Class PresetData
    Public Property SchemaVersion As Integer = 1
    Public Property Name As String = ""
    Public Property VideoEncoder As String = "libx264"
    Public Property AudioEncoder As String = "aac"
    Public Property Container As String = "mp4"
    Public Property IncludesVideo As Boolean = True
    Public Property IncludesAudio As Boolean = True
    Public Property Modules As New List(Of PresetModule)()
End Class


''' <summary>
''' 方案的读写。
'''
''' 只存「模块 id + 参数值」，不存模块定义本身——定义永远来自 modules.json，
''' 这样以后模块的说明、默认值、binding 更新了，老方案也能跟着受益。
''' </summary>
Public Module PresetService

    Private ReadOnly JsonOpts As New JsonSerializerOptions With {
        .WriteIndented = True,
        .PropertyNameCaseInsensitive = True,
        .Encoder = JavaScriptEncoderSafe
    }

    ''' <summary>让中文说明在 JSON 里保持可读，不转义成 \\uXXXX。</summary>
    Private ReadOnly Property JavaScriptEncoderSafe As System.Text.Encodings.Web.JavaScriptEncoder
        Get
            Return System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        End Get
    End Property

    Public Function Capture(vm As MainViewModel) As PresetData
        Dim data As New PresetData With {
            .Name = "未命名方案",
            .VideoEncoder = vm.VideoEncoder,
            .AudioEncoder = vm.AudioEncoder,
            .Container = vm.Container,
            .IncludesVideo = vm.IncludesVideo,
            .IncludesAudio = vm.IncludesAudio
        }

        For Each inst In vm.VideoInstances.Concat(vm.AudioInstances)
            Dim pm As New PresetModule With {.Id = inst.Definition.Id}
            For Each kv In inst.Values
                pm.Values(kv.Key) = kv.Value
            Next
            data.Modules.Add(pm)
        Next

        Return data
    End Function

    Public Sub Save(vm As MainViewModel, filePath As String)
        Dim data = Capture(vm)
        data.Name = System.IO.Path.GetFileNameWithoutExtension(filePath)

        Dim dir = System.IO.Path.GetDirectoryName(filePath)
        If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
            Directory.CreateDirectory(dir)
        End If

        File.WriteAllText(filePath, JsonSerializer.Serialize(data, JsonOpts), Text.Encoding.UTF8)
    End Sub

    ''' <summary>加载方案并套用到视图模型。返回 false 表示文件不可读或格式不对。</summary>
    Public Function Load(vm As MainViewModel, filePath As String) As Boolean
        If Not File.Exists(filePath) Then Return False

        Dim data As PresetData
        Try
            data = JsonSerializer.Deserialize(Of PresetData)(File.ReadAllText(filePath), JsonOpts)
        Catch
            Return False
        End Try
        If data Is Nothing Then Return False

        vm.ApplyPreset(data)
        Return True
    End Function
End Module
