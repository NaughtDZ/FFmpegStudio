Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Threading

''' <summary>用 ffprobe 读到的输入媒体信息。</summary>
Public Class MediaInfo
    Public Property Path As String = ""
    Public Property DurationMs As Long
    Public Property HasVideo As Boolean
    Public Property HasAudio As Boolean
    Public Property Width As Integer
    Public Property Height As Integer
    Public Property Fps As Double
    Public Property VideoCodec As String = ""
    Public Property AudioCodec As String = ""

    Public ReadOnly Property DurationText As String
        Get
            Dim ts = TimeSpan.FromMilliseconds(DurationMs)
            Return ts.ToString("mm\:ss")
        End Get
    End Property

    Public ReadOnly Property ResolutionText As String
        Get
            If Not HasVideo Then Return "无视频流"
            Return $"{Width}×{Height}"
        End Get
    End Property
End Class


''' <summary>
''' 输入文件探测。用 ffprobe 读真实流信息——这是「纯音频文件选了仅视频」能被提前拦住的前提。
''' </summary>
Public Module MediaProbe

    Public Function Probe(ffprobePath As String, path As String) As MediaInfo
        Dim info As New MediaInfo With {.Path = path}
        If Not File.Exists(path) Then Return info

        Dim args = $"-v error -show_entries format=duration:stream=codec_type,codec_name,width,height,r_frame_rate -of json ""{path}"""

        Dim psi As New ProcessStartInfo(ffprobePath, args) With {
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .StandardOutputEncoding = Encoding.UTF8
        }

        Try
            Using p = Process.Start(psi)
                Dim json = p.StandardOutput.ReadToEnd()
                p.WaitForExit(10000)

                Using doc = JsonDocument.Parse(json)
                    Dim root = doc.RootElement

                    Dim fmt As JsonElement
                    If root.TryGetProperty("format", fmt) Then
                        Dim d As JsonElement
                        If fmt.TryGetProperty("duration", d) Then
                            Dim sec As Double
                            If Double.TryParse(d.GetString(), Globalization.NumberStyles.Float,
                                               Globalization.CultureInfo.InvariantCulture, sec) Then
                                info.DurationMs = CLng(sec * 1000)
                            End If
                        End If
                    End If

                    Dim streams As JsonElement
                    If root.TryGetProperty("streams", streams) Then
                        For Each st In streams.EnumerateArray()
                            Dim ct As JsonElement
                            If Not st.TryGetProperty("codec_type", ct) Then Continue For

                            Select Case ct.GetString()
                                Case "video"
                                    info.HasVideo = True
                                    Dim w, h As JsonElement
                                    If st.TryGetProperty("width", w) Then info.Width = w.GetInt32()
                                    If st.TryGetProperty("height", h) Then info.Height = h.GetInt32()
                                    Dim cn As JsonElement
                                    If st.TryGetProperty("codec_name", cn) Then info.VideoCodec = cn.GetString()
                                    Dim fr As JsonElement
                                    If st.TryGetProperty("r_frame_rate", fr) Then
                                        Dim parts = fr.GetString().Split("/"c)
                                        If parts.Length = 2 Then
                                            Dim num, den As Double
                                            If Double.TryParse(parts(0), num) AndAlso Double.TryParse(parts(1), den) AndAlso den > 0 Then
                                                info.Fps = Math.Round(num / den, 2)
                                            End If
                                        End If
                                    End If
                                Case "audio"
                                    info.HasAudio = True
                                    Dim cn As JsonElement
                                    If st.TryGetProperty("codec_name", cn) Then info.AudioCodec = cn.GetString()
                            End Select
                        Next
                    End If
                End Using
            End Using
        Catch
            ' 探测失败不影响主流程：时长未知只会让进度条变成「不确定」模式
        End Try

        Return info
    End Function
End Module


''' <summary>一次转码的实时进度。</summary>
Public Class RunProgress
    Public Property Frame As Long
    Public Property Fps As Double
    Public Property Bitrate As String = ""
    Public Property TotalSizeBytes As Long
    Public Property OutTimeMs As Long
    Public Property Speed As String = ""
    Public Property Percent As Integer = -1        ' -1 表示无法计算（时长未知）
    Public Property EtaSeconds As Integer = -1

    Public ReadOnly Property SizeText As String
        Get
            If TotalSizeBytes <= 0 Then Return "—"
            If TotalSizeBytes > 1024 * 1024 Then Return $"{TotalSizeBytes / 1024.0 / 1024.0:0.0} MB"
            Return $"{TotalSizeBytes / 1024.0:0} KB"
        End Get
    End Property

    Public ReadOnly Property EtaText As String
        Get
            If EtaSeconds < 0 Then Return "—"
            Return TimeSpan.FromSeconds(EtaSeconds).ToString("mm\:ss")
        End Get
    End Property
End Class


''' <summary>一次转码的结果。</summary>
Public Class RunResult
    Public Property Success As Boolean
    Public Property Canceled As Boolean
    Public Property ExitCode As Integer
    Public Property ErrorText As String = ""
    Public Property ElapsedMs As Long
End Class


''' <summary>
''' ffmpeg 进程封装。
'''
''' 关键设计：
'''   · 用 -progress pipe:1 -nostats，进度走 stdout（key=value 流），日志走 stderr，两者分开不打架；
'''   · stderr 只保留尾部 200 行，失败时原样给用户看，而不是"转换失败"四个字；
'''   · 取消时 Kill 整个进程树，并等待退出，避免留下孤儿 ffmpeg。
''' </summary>
Public Class FfmpegRunner

    Public Event Progress(p As RunProgress)
    Public Event Log(line As String)

    Private ReadOnly _stderrTail As New List(Of String)
    Private ReadOnly _gate As New Object()

    Public Async Function RunAsync(ffmpegPath As String,
                                   args As IList(Of String),
                                   durationMs As Long,
                                   progress As IProgress(Of RunProgress),
                                   ct As CancellationToken) As Task(Of RunResult)

        Dim sw = Stopwatch.StartNew()
        Dim result As New RunResult()

        Dim psi As New ProcessStartInfo(ffmpegPath) With {
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8
        }
        For Each a In args
            psi.ArgumentList.Add(a)
        Next

        Dim proc As Process
        Try
            proc = Process.Start(psi)
        Catch ex As Exception
            result.ExitCode = -1
            result.ErrorText = "无法启动 ffmpeg：" & ex.Message
            result.ElapsedMs = sw.ElapsedMilliseconds
            Return result
        End Try

        Dim state As New ProgressAccumulator()

        AddHandler proc.OutputDataReceived,
            Sub(s, e)
                If e.Data Is Nothing Then Return
                If state.Feed(e.Data) Then
                    Dim p = state.Build(durationMs)
                    progress?.Report(p)
                End If
            End Sub

        AddHandler proc.ErrorDataReceived,
            Sub(s, e)
                If e.Data Is Nothing Then Return
                SyncLock _gate
                    _stderrTail.Add(e.Data)
                    If _stderrTail.Count > 200 Then _stderrTail.RemoveAt(0)
                End SyncLock
                RaiseEvent Log(e.Data)
            End Sub

        proc.BeginOutputReadLine()
        proc.BeginErrorReadLine()

        Using reg = ct.Register(
            Sub()
                Try
                    If Not proc.HasExited Then proc.Kill(entireProcessTree:=True)
                Catch
                End Try
            End Sub)

            ' 取消时 WaitForExitAsync 会抛 OperationCanceledException，这里吞掉——
            ' 进程已在 ct.Register 里被 Kill，具体结果由下面的 Canceled 判定给出
            Try
                Await proc.WaitForExitAsync(ct)
            Catch ex As OperationCanceledException
            End Try
        End Using

        Try
            If Not proc.HasExited Then proc.WaitForExit(3000)
        Catch
        End Try

        result.ElapsedMs = sw.ElapsedMilliseconds
        result.Canceled = ct.IsCancellationRequested
        result.ExitCode = If(proc.HasExited, proc.ExitCode, -1)
        result.Success = (Not result.Canceled) AndAlso result.ExitCode = 0

        If Not result.Success Then
            SyncLock _gate
                result.ErrorText = String.Join(Environment.NewLine, _stderrTail.TakeLast(12))
            End SyncLock
        End If

        Try
            proc.Dispose()
        Catch
        End Try

        Return result
    End Function


    ''' <summary>累积 stdout 的 key=value 进度行，遇到 progress=continue/end 时吐一次。</summary>
    Private Class ProgressAccumulator
        Private _frame As Long
        Private _fps As Double
        Private _bitrate As String = ""
        Private _size As Long
        Private _outTimeMs As Long
        Private _speed As String = ""

        Public Function Feed(line As String) As Boolean
            Dim idx = line.IndexOf("="c)
            If idx <= 0 Then Return False

            Dim key = line.Substring(0, idx).Trim()
            Dim value = line.Substring(idx + 1).Trim()

            Select Case key
                Case "frame" : Long.TryParse(value, _frame)
                Case "fps" : Double.TryParse(value, Globalization.NumberStyles.Float,
                                             Globalization.CultureInfo.InvariantCulture, _fps)
                Case "bitrate" : _bitrate = value
                Case "total_size" : Long.TryParse(value, _size)
                Case "out_time_ms" : Long.TryParse(value, _outTimeMs)
                Case "speed" : _speed = value
                Case "progress"
                    Return True
            End Select
            Return False
        End Function

        Public Function Build(durationMs As Long) As RunProgress
            Dim p As New RunProgress With {
                .Frame = _frame,
                .Fps = _fps,
                .Bitrate = _bitrate,
                .TotalSizeBytes = _size,
                .OutTimeMs = _outTimeMs,
                .Speed = _speed
            }

            If durationMs > 0 Then
                Dim pct = CInt(Math.Min(100, Math.Max(0, _outTimeMs * 100.0 / durationMs)))
                p.Percent = pct
                ' 用 ffmpeg 自己报的 speed 估剩余时间，比按已用时间线性外推准
                Dim speedNum As Double = 0
                If Double.TryParse(_speed.Replace("x", ""), Globalization.NumberStyles.Float,
                                   Globalization.CultureInfo.InvariantCulture, speedNum) AndAlso speedNum > 0.01 Then
                    Dim remainMs = Math.Max(0, durationMs - _outTimeMs)
                    p.EtaSeconds = CInt(remainMs / 1000.0 / speedNum)
                End If
            End If

            Return p
        End Function
    End Class
End Class
