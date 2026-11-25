''' <summary>
''' 输出容器（封装格式）。
'''
''' 容器不是随便选的：它决定能不能装视频、能不能装音频、以及音频编码器有哪些合法值。
''' 比如 mp3 容器只认 libmp3lame，先选 mp4 当容器再挑 libmp3lame 是没意义的。
''' </summary>
Public Class ContainerDef
    Public Property Name As String = ""

    ''' <summary>输出文件扩展名，含点。</summary>
    Public Property Extension As String = ""

    Public Property HasVideo As Boolean
    Public Property HasAudio As Boolean

    ''' <summary>允许的音频编码器，逗号分隔；空字符串表示不限制。</summary>
    Public Property AudioCodecs As String = ""

    Public ReadOnly Property IsAudioOnly As Boolean
        Get
            Return HasAudio AndAlso Not HasVideo
        End Get
    End Property

    ''' <summary>拆成编码器名列表；空表示不限。</summary>
    Public ReadOnly Property AllowedAudioCodecs As String()
        Get
            If String.IsNullOrWhiteSpace(AudioCodecs) Then Return Array.Empty(Of String)()
            Return AudioCodecs.Split(","c).Select(Function(x) x.Trim()).Where(Function(x) x.Length > 0).ToArray()
        End Get
    End Property
End Class


Public Module Containers

    ' 每个元素写成一行：VB 的对象初始化器跨行容易出语法问题，不图省字符
    Public ReadOnly All As ContainerDef() = {
        New ContainerDef With {.Name = "mp4", .Extension = ".mp4", .HasVideo = True, .HasAudio = True},
        New ContainerDef With {.Name = "mkv", .Extension = ".mkv", .HasVideo = True, .HasAudio = True},
        New ContainerDef With {.Name = "mov", .Extension = ".mov", .HasVideo = True, .HasAudio = True},
        New ContainerDef With {.Name = "webm", .Extension = ".webm", .HasVideo = True, .HasAudio = True},
        New ContainerDef With {.Name = "m4a", .Extension = ".m4a", .HasVideo = False, .HasAudio = True, .AudioCodecs = "aac,alac"},
        New ContainerDef With {.Name = "mp3", .Extension = ".mp3", .HasVideo = False, .HasAudio = True, .AudioCodecs = "libmp3lame"},
        New ContainerDef With {.Name = "flac", .Extension = ".flac", .HasVideo = False, .HasAudio = True, .AudioCodecs = "flac"},
        New ContainerDef With {.Name = "wav", .Extension = ".wav", .HasVideo = False, .HasAudio = True, .AudioCodecs = "pcm_s16le,pcm_s24le,pcm_u8"},
        New ContainerDef With {.Name = "ogg", .Extension = ".ogg", .HasVideo = False, .HasAudio = True, .AudioCodecs = "libvorbis,libopus"},
        New ContainerDef With {.Name = "opus", .Extension = ".opus", .HasVideo = False, .HasAudio = True, .AudioCodecs = "libopus"},
        New ContainerDef With {.Name = "aac", .Extension = ".aac", .HasVideo = False, .HasAudio = True, .AudioCodecs = "aac"},
        New ContainerDef With {.Name = "wma", .Extension = ".wma", .HasVideo = False, .HasAudio = True, .AudioCodecs = "wmav2"}
    }

    Public Function Find(name As String) As ContainerDef
        If String.IsNullOrEmpty(name) Then Return Nothing
        Return All.FirstOrDefault(Function(c) String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
    End Function

    Public Function Names() As IEnumerable(Of String)
        Return All.Select(Function(c) c.Name)
    End Function

    ''' <summary>只列视频容器。</summary>
    Public Function VideoContainerNames() As IEnumerable(Of String)
        Return All.Where(Function(c) c.HasVideo).Select(Function(c) c.Name)
    End Function
End Module
