Imports System.IO

''' <summary>
''' 定位 ffmpeg / ffprobe。
'''
''' 查找顺序：
'''   1. 程序目录下的 ffmpeg\ffmpeg.exe —— 发行包内置，开箱即用
'''   2. PATH —— 开发者自己装了 ffmpeg 的情况
'''
''' 之所以不写死绝对路径：发行版必须能在任何机器上跑，而且能力表是从「内置的这一个
''' ffmpeg」烘焙出来的，用错版本会导致能力表和实际行为对不上。
''' </summary>
Public Module FfmpegLocator

    Private _ffmpeg As String
    Private _ffprobe As String
    Private _isBundled As Boolean

    Private ReadOnly Gate As New Object()

    Public ReadOnly Property Ffmpeg As String
        Get
            EnsureResolved()
            Return _ffmpeg
        End Get
    End Property

    Public ReadOnly Property Ffprobe As String
        Get
            EnsureResolved()
            Return _ffprobe
        End Get
    End Property

    ''' <summary>true = 用的是内置 ffmpeg；false = 退回到 PATH。</summary>
    Public ReadOnly Property IsBundled As Boolean
        Get
            EnsureResolved()
            Return _isBundled
        End Get
    End Property

    Private Sub EnsureResolved()
        If _ffmpeg IsNot Nothing Then Return
        SyncLock Gate
            If _ffmpeg IsNot Nothing Then Return

            Dim appDir = AppContext.BaseDirectory
            Dim bundledDir = Path.Combine(appDir, "ffmpeg")
            Dim bundledFfmpeg = Path.Combine(bundledDir, "ffmpeg.exe")

            If File.Exists(bundledFfmpeg) Then
                _ffmpeg = bundledFfmpeg
                Dim bp = Path.Combine(bundledDir, "ffprobe.exe")
                _ffprobe = If(File.Exists(bp), bp, "ffprobe")
                _isBundled = True
            Else
                _ffmpeg = "ffmpeg"
                _ffprobe = "ffprobe"
                _isBundled = False
            End If
        End SyncLock
    End Sub

    ''' <summary>供 UI 显示的一句话说明。</summary>
    Public Function Describe() As String
        EnsureResolved()
        If _isBundled Then Return "内置 ffmpeg"
        Return "系统 PATH 中的 ffmpeg"
    End Function
End Module
