Imports System.Diagnostics
Imports System.Threading.Tasks

''' <summary>
''' 硬件编码器的运行时可用性探测。
'''
''' 为什么需要它：构建期烘焙的 capabilities.json 只能证明「这个 ffmpeg 二进制编译进了
''' h264_nvenc」，证明不了「这台机器有 NVIDIA 显卡、驱动版本够」。没有这一步，用户会在
''' 一台没有 N 卡的机器上看到 NVENC 选项，选了直接报错——比选项不存在更糟。
'''
''' 做法：真的拿 ffmpeg 初始化一次编码器（编 0.1 秒的 64×64 黑帧到 null），
''' 退出码为 0 才算可用。这是唯一可靠的判据。
''' </summary>
Public Module HardwareProbe

    ''' <summary>Windows 上常见的硬件编码器。只探测能力表里确实存在的。</summary>
    Public ReadOnly Candidates As String() = {
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_qsv", "hevc_qsv", "av1_qsv",
        "h264_amf", "hevc_amf"
    }

    ''' <summary>逐个试初始化，返回这台机器上真正能用的编码器。</summary>
    Public Function Detect(capabilities As Capabilities, perEncoderTimeoutMs As Integer) As HashSet(Of String)
        Dim usable As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If capabilities Is Nothing Then Return usable

        For Each name In Candidates
            If capabilities.FindEncoder(name) Is Nothing Then Continue For   ' 这个 ffmpeg 压根没编进去
            If CanInitialize(name, perEncoderTimeoutMs) Then usable.Add(name)
        Next

        Return usable
    End Function

    Private Function CanInitialize(encoder As String, timeoutMs As Integer) As Boolean
        Dim psi As New ProcessStartInfo(FfmpegLocator.Ffmpeg) With {
            .RedirectStandardError = True,
            .RedirectStandardOutput = True,
            .UseShellExecute = False,
            .CreateNoWindow = True
        }
        psi.ArgumentList.Add("-hide_banner")
        psi.ArgumentList.Add("-loglevel")
        psi.ArgumentList.Add("error")
        psi.ArgumentList.Add("-f")
        psi.ArgumentList.Add("lavfi")
        psi.ArgumentList.Add("-i")
        psi.ArgumentList.Add("color=c=black:s=64x64:d=0.1")
        psi.ArgumentList.Add("-c:v")
        psi.ArgumentList.Add(encoder)
        psi.ArgumentList.Add("-f")
        psi.ArgumentList.Add("null")
        psi.ArgumentList.Add("-")

        Try
            Using p = Process.Start(psi)
                ' 必须把输出读空，否则子进程可能写满管道后卡死
                Dim err = p.StandardError.ReadToEnd()
                Dim outp = p.StandardOutput.ReadToEnd()

                If Not p.WaitForExit(timeoutMs) Then
                    Try
                        p.Kill(entireProcessTree:=True)
                    Catch
                    End Try
                    Return False
                End If

                Return p.ExitCode = 0
            End Using
        Catch
            Return False
        End Try
    End Function
End Module
