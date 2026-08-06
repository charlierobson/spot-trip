import Foundation
import AVFoundation
import CoreAudio
import AudioToolbox

// MARK: - State machine

enum RecorderState {
    case waiting       // expecting silence up to 15s before audio
    case recording     // audio above threshold detected
    case finishing     // gone silent, waiting up to 5s for it to end
    case done
}

// MARK: - Silence thresholds / timeouts

// Some useful values:
// dBFS	RMS
// -50	0.00316 (current)
// -55	0.00178
// -60	0.001
// -65	0.000562
// -70	0.000316

// dBFS = 20 × log₁₀(rms)
// rms = 10^(dBFS / 20)

let kSilenceRMS: Float         = 0.000316
let kWaitingTimeout: Double    = 15.0    // give up waiting for audio after 15s
let kRecordingToFinishing: Double =  3.0    // silence needed to enter finishing
let kFinishingSilence: Double  =  5.0    // total silence before stop
let kLongSilence: Double       = 20.0    // abort if silent for 20s during recording
let kOverrunFactor: Double     =  1.25   // abort if 25% over hint

// MARK: - WAV writer

struct WAVWriter {
    static func write(url: URL, pcm: [Float], sampleRate: Double, channels: Int, bitDepth: Int) throws {
        var data = Data()

        let bytesPerSample = bitDepth / 8
        let numSamples = pcm.count
        let dataSize = numSamples * bytesPerSample
        let fileSize = 36 + dataSize

        func le16(_ v: Int) -> [UInt8] { [UInt8(v & 0xff), UInt8((v >> 8) & 0xff)] }
        func le32(_ v: Int) -> [UInt8] {
            [UInt8(v & 0xff), UInt8((v>>8)&0xff), UInt8((v>>16)&0xff), UInt8((v>>24)&0xff)]
        }

        // RIFF header
        data.append(contentsOf: "RIFF".utf8)
        data.append(contentsOf: le32(fileSize))
        data.append(contentsOf: "WAVE".utf8)
        // fmt chunk
        data.append(contentsOf: "fmt ".utf8)
        data.append(contentsOf: le32(16))                          // chunk size
        data.append(contentsOf: le16(1))                           // PCM
        data.append(contentsOf: le16(channels))
        data.append(contentsOf: le32(Int(sampleRate)))
        let byteRate = Int(sampleRate) * channels * bytesPerSample
        data.append(contentsOf: le32(byteRate))
        data.append(contentsOf: le16(channels * bytesPerSample))   // block align
        data.append(contentsOf: le16(bitDepth))
        // data chunk
        data.append(contentsOf: "data".utf8)
        data.append(contentsOf: le32(dataSize))

        // Samples
        for s in pcm {
            let clamped = max(-1.0, min(1.0, s))
            switch bitDepth {
            case 16:
                let v = Int16(clamped < 0 ? clamped * 32768.0 : clamped * 32767.0)
                withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) }
            case 24:
                let v = Int32(clamped < 0 ? clamped * 8388608.0 : clamped * 8388607.0)
                data.append(UInt8(v & 0xff))
                data.append(UInt8((v >> 8) & 0xff))
                data.append(UInt8((v >> 16) & 0xff))
            default: // 32-bit
                let v = Int32(clamped < 0 ? clamped * 2147483648.0 : clamped * 2147483647.0)
                withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) }
            }
        }

        try data.write(to: url)
    }
}

// MARK: - Device lookup

func findDevice(named name: String) -> AudioDeviceID? {
    var propSize: UInt32 = 0
    var addr = AudioObjectPropertyAddress(
        mSelector: kAudioHardwarePropertyDevices,
        mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain)

    guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &propSize) == noErr else { return nil }
    let count = Int(propSize) / MemoryLayout<AudioDeviceID>.size
    var devices = [AudioDeviceID](repeating: 0, count: count)
    guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &propSize, &devices) == noErr else { return nil }

    for deviceID in devices {
        // Check it has input channels
        var inputAddr = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioDevicePropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain)
        var inputSize: UInt32 = 0
        AudioObjectGetPropertyDataSize(deviceID, &inputAddr, 0, nil, &inputSize)
        if inputSize == 0 { continue }

        // Get name
        var nameAddr = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceNameCFString,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var cfNameRef: Unmanaged<CFString>? = nil
        var nameSize = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        AudioObjectGetPropertyData(deviceID, &nameAddr, 0, nil, &nameSize, &cfNameRef)
        let deviceName = cfNameRef?.takeRetainedValue() as String? ?? ""
        if deviceName.lowercased().contains(name.lowercased()) {
            return deviceID
        }
    }
    return nil
}

func listDevices() {
    var propSize: UInt32 = 0
    var addr = AudioObjectPropertyAddress(
        mSelector: kAudioHardwarePropertyDevices,
        mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain)
    AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &propSize)
    let count = Int(propSize) / MemoryLayout<AudioDeviceID>.size
    var devices = [AudioDeviceID](repeating: 0, count: count)
    AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &propSize, &devices)

    print("Available input devices:")
    for deviceID in devices {
        var inputAddr = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioDevicePropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain)
        var inputSize: UInt32 = 0
        AudioObjectGetPropertyDataSize(deviceID, &inputAddr, 0, nil, &inputSize)
        guard inputSize > 0 else { continue }

        var nameAddr = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceNameCFString,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var cfNameRef: Unmanaged<CFString>? = nil
        var nameSize = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        AudioObjectGetPropertyData(deviceID, &nameAddr, 0, nil, &nameSize, &cfNameRef)
        let name = cfNameRef?.takeRetainedValue() as String? ?? "(unknown)"
        print("  \(name)")
    }
}

func getDeviceFormat(deviceID: AudioDeviceID) -> (sampleRate: Double, channels: Int, bitDepth: Int) {
    var addr = AudioObjectPropertyAddress(
        mSelector: kAudioDevicePropertyNominalSampleRate,
        mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain)
    var sampleRate: Float64 = 44100
    var size = UInt32(MemoryLayout<Float64>.size)
    AudioObjectGetPropertyData(deviceID, &addr, 0, nil, &size, &sampleRate)

    var streamAddr = AudioObjectPropertyAddress(
        mSelector: kAudioDevicePropertyStreams,
        mScope: kAudioDevicePropertyScopeInput,
        mElement: kAudioObjectPropertyElementMain)
    var streamSize: UInt32 = 0
    AudioObjectGetPropertyDataSize(deviceID, &streamAddr, 0, nil, &streamSize)
    var streamIDs = [AudioStreamID](repeating: 0, count: Int(streamSize) / MemoryLayout<AudioStreamID>.size)
    AudioObjectGetPropertyData(deviceID, &streamAddr, 0, nil, &streamSize, &streamIDs)

    var bitDepth = 16
    var channels = 2
    if let firstStream = streamIDs.first {
        var fmtAddr = AudioObjectPropertyAddress(
            mSelector: kAudioStreamPropertyPhysicalFormat,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var asbd = AudioStreamBasicDescription()
        var fmtSize = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        if AudioObjectGetPropertyData(firstStream, &fmtAddr, 0, nil, &fmtSize, &asbd) == noErr {
            bitDepth = Int(asbd.mBitsPerChannel) > 0 ? Int(asbd.mBitsPerChannel) : 16
            channels = Int(asbd.mChannelsPerFrame) > 0 ? Int(asbd.mChannelsPerFrame) : 2
        }
    }
    return (Double(sampleRate), channels, bitDepth)
}

// MARK: - RMS

func rms(_ buffer: AVAudioPCMBuffer) -> Float {
    guard let data = buffer.floatChannelData else { return 0 }
    let frameCount = Int(buffer.frameLength)
    guard frameCount > 0 else { return 0 }
    var sum: Float = 0
    let ch = Int(buffer.format.channelCount)
    for c in 0..<ch {
        let ptr = data[c]
        for i in 0..<frameCount {
            sum += ptr[i] * ptr[i]
        }
    }
    return sqrt(sum / Float(frameCount * ch))
}

// MARK: - Console display

class Display {
    var state: RecorderState = .waiting
    var elapsed: Double = 0
    var hint: Double = 0

    private var lastLine = ""

    func update(state: RecorderState, elapsed: Double) {
        self.state = state
        self.elapsed = elapsed
        let label: String
        switch state {
        case .waiting:    label = "Waiting   "
        case .recording:  label = "Recording "
        case .finishing:  label = "Finishing "
        case .done:       label = "Done      "
        }
        let mins = Int(elapsed) / 60
        let secs = Int(elapsed) % 60
        let tenths = Int(elapsed * 10) % 10
        let timeStr = String(format: "%d:%02d.%d", mins, secs, tenths)
        let line = "\r[\(label)] \(timeStr)"
        if line != lastLine {
            print(line, terminator: "")
            fflush(stdout)
            lastLine = line
        }
    }

    func finish() {
        print("")
    }
}

// MARK: - Main recorder

class Recorder {
    let outputURL: URL
    let durationHint: Double
    let deviceName: String

    private var engine: AVAudioEngine?
    private var buffer: [Float] = []
    private let bufferLock = NSLock()
    private var state: RecorderState = .waiting
    private var stateStart: Date = Date()
    private var recordingStart: Date?
    private var silenceStart: Date?
    private var deviceFormat: (sampleRate: Double, channels: Int, bitDepth: Int) = (44100, 2, 16)
    private var displayTimer: Timer?
    private let display = Display()
    private var startTime = Date()

    init(outputURL: URL, durationHint: Double, deviceName: String) {
        self.outputURL = outputURL
        self.durationHint = durationHint
        self.deviceName = deviceName
    }

    func run() -> Int32 {
        fputs("recorder v1.0.1\n", stdout);
        
        // Find device
        guard let deviceID = findDevice(named: deviceName) else {
            fputs("Error: no input device found matching '\(deviceName)'\n", stderr)
            fputs("", stderr)
            listDevices()
            return 1
        }

        deviceFormat = getDeviceFormat(deviceID: deviceID)

        // Set engine input device
        let engine = AVAudioEngine()
        self.engine = engine
        let inputNode = engine.inputNode

        // Set the hardware device
        var deviceIDVar = deviceID
        let unitAddr = AudioComponentDescription(
            componentType: kAudioUnitType_Output,
            componentSubType: kAudioUnitSubType_HALOutput,
            componentManufacturer: kAudioUnitManufacturer_Apple,
            componentFlags: 0,
            componentFlagsMask: 0)
        _ = unitAddr  // used below via AudioUnit property
        AudioUnitSetProperty(
            inputNode.audioUnit!,
            kAudioOutputUnitProperty_CurrentDevice,
            kAudioUnitScope_Global,
            0,
            &deviceIDVar,
            UInt32(MemoryLayout<AudioDeviceID>.size))

        let format = inputNode.outputFormat(forBus: 0)
        display.hint = durationHint
        startTime = Date()

        inputNode.installTap(onBus: 0, bufferSize: 4096, format: format) { [weak self] (buf, _) in
            self?.handleBuffer(buf)
        }

        do {
            try engine.start()
        } catch {
            fputs("Error starting audio engine: \(error)\n", stderr)
            return 1
        }

        // Only now is the tap actually live — this is the true readiness signal
        // the caller waits on before starting playback.
        print("Device: \(deviceName) | \(Int(deviceFormat.sampleRate))Hz | \(deviceFormat.channels)ch | \(deviceFormat.bitDepth)-bit")
        fflush(stdout)

        startStdinListener()

        // Display timer
        let runLoop = RunLoop.current
        let timer = Timer(timeInterval: 1.0, repeats: true) { [weak self] _ in
            guard let self = self else { return }
            let elapsed = Date().timeIntervalSince(self.startTime)
            self.display.update(state: self.state, elapsed: elapsed)
        }
        runLoop.add(timer, forMode: .common)
        displayTimer = timer

        // Wait loop
        while state != .done {
            runLoop.run(mode: .default, before: Date(timeIntervalSinceNow: 0.05))
        }

        timer.invalidate()
        display.update(state: .done, elapsed: Date().timeIntervalSince(startTime))
        display.finish()

        engine.inputNode.removeTap(onBus: 0)
        engine.stop()

        // Write output
        return writeOutput()
    }

    // Listens for line-based commands on stdin so the caller can control
    // the recorder explicitly instead of relying solely on silence detection.
    // Currently only STOP is supported; more commands can be added here later.
    private func startStdinListener() {
        Thread.detachNewThread { [weak self] in
            while let line = readLine(strippingNewline: true) {
                guard let self = self else { return }
                switch line.trimmingCharacters(in: .whitespaces) {
                case "STOP":
                    if self.state != .done {
                        self.state = .done
                    }
                    return
                default:
                    continue
                }
            }
        }
    }

    private func handleBuffer(_ buf: AVAudioPCMBuffer) {
        let level = rms(buf)
        let now = Date()

        // Flatten to interleaved Float32 for storage
        guard let data = buf.floatChannelData else { return }
        let frames = Int(buf.frameLength)
        let ch = Int(buf.format.channelCount)
        var samples = [Float](repeating: 0, count: frames * ch)
        for f in 0..<frames {
            for c in 0..<ch { samples[f * ch + c] = data[c][f] }
        }

        // Accumulate once recording has started
        if state == .recording || state == .finishing {
            bufferLock.lock()
            buffer.append(contentsOf: samples)
            bufferLock.unlock()
        }

        // Track silence continuously across states
        if level > kSilenceRMS {
            silenceStart = nil
        } else if silenceStart == nil, state == .recording || state == .finishing {
            silenceStart = now
        }

        let silenceDur = silenceStart.map { now.timeIntervalSince($0) } ?? 0

        switch state {
        case .waiting:
            if level > kSilenceRMS {
                state = .recording
                stateStart = now
                recordingStart = now
                // include this first buffer
                bufferLock.lock()
                buffer.append(contentsOf: samples)
                bufferLock.unlock()
            } else if now.timeIntervalSince(stateStart) > kWaitingTimeout {
                fputs("\nNo audio detected in \(Int(kWaitingTimeout))s — aborting.\n", stderr)
                state = .done
            }

        case .recording:
            // Enter finishing after 3s silence (total 5s silence → done in finishing)
            if silenceDur >= kRecordingToFinishing {
                state = .finishing
                stateStart = now
            }
            // Overrun check
            if let rs = recordingStart, now.timeIntervalSince(rs) > durationHint * kOverrunFactor {
                fputs("\nExceeded duration limit — stopping.\n", stderr)
                state = .done
            }

        case .finishing:
            if level > kSilenceRMS {
                // Audio resumed
                state = .recording
                stateStart = now
            } else if silenceDur >= kFinishingSilence {
                // 5s total continuous silence — done
                state = .done
            }

        case .done:
            break
        }

        // Safety abort: >20s continuous silence during/after recording
        if silenceDur >= kLongSilence, state != .done, state != .waiting {
            fputs("\n20s of silence — aborting.\n", stderr)
            state = .done
        }
    }

    private func writeOutput() -> Int32 {
        var samples: [Float]
        bufferLock.lock()
        samples = buffer
        bufferLock.unlock()

        if samples.isEmpty {
            fputs("No audio captured.\n", stderr)
            return 1
        }

        let ch = Int(deviceFormat.channels) > 0 ? Int(deviceFormat.channels) : 2
        let sr = deviceFormat.sampleRate

        // Trim leading silence
        let blockSize = ch * 512
        var leadEnd = 0
        while leadEnd + blockSize <= samples.count {
            let block = Array(samples[leadEnd..<leadEnd+blockSize])
            let r = sqrt(block.map { $0*$0 }.reduce(0, +) / Float(blockSize))
            if r > kSilenceRMS { break }
            leadEnd += blockSize
        }

        // Trim trailing silence
        var trailStart = samples.count
        while trailStart - blockSize >= leadEnd {
            let block = Array(samples[trailStart-blockSize..<trailStart])
            let r = sqrt(block.map { $0*$0 }.reduce(0, +) / Float(blockSize))
            if r > kSilenceRMS { break }
            trailStart -= blockSize
        }

        let trimmed = Array(samples[leadEnd..<trailStart])
        if trimmed.isEmpty {
            fputs("After silence trim, no audio remains.\n", stderr)
            return 1
        }

        let durSec = Double(trimmed.count / ch) / sr
        let ext = outputURL.pathExtension.lowercased()
        let bitDepth: Int
        switch deviceFormat.bitDepth {
        case 24: bitDepth = 24
        case 32: bitDepth = 32
        default: bitDepth = 16
        }

        func uniqueURL(_ url: URL) -> URL {
            guard FileManager.default.fileExists(atPath: url.path) else { return url }
            let dir  = url.deletingLastPathComponent()
            let stem = url.deletingPathExtension().lastPathComponent
            let ext  = url.pathExtension
            var n = 1
            var candidate: URL
            repeat {
                candidate = dir.appendingPathComponent("\(stem) (\(n))").appendingPathExtension(ext)
                n += 1
            } while FileManager.default.fileExists(atPath: candidate.path)
            return candidate
        }

        print("Saving \(String(format: "%.1f", durSec))s of audio to \(outputURL.lastPathComponent)...")
        do {
            let finalURL = uniqueURL(outputURL)
            if ext == "flac" {
                let format = AVAudioFormat(commonFormat: .pcmFormatFloat32,
                                           sampleRate: sr,
                                           channels: AVAudioChannelCount(ch),
                                           interleaved: false)!
                let settings: [String: Any] = [
                    AVFormatIDKey: kAudioFormatFLAC,
                    AVSampleRateKey: sr,
                    AVNumberOfChannelsKey: ch,
                    AVLinearPCMBitDepthKey: bitDepth
                ]
                let file = try AVAudioFile(forWriting: finalURL, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
                let frameCount = trimmed.count / ch
                let pcmBuf = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frameCount))!
                pcmBuf.frameLength = AVAudioFrameCount(frameCount)
                for c in 0..<ch {
                    let dst = pcmBuf.floatChannelData![c]
                    for f in 0..<frameCount { dst[f] = trimmed[f * ch + c] }
                }
                try file.write(from: pcmBuf)
            } else {
                try WAVWriter.write(url: finalURL, pcm: trimmed, sampleRate: sr, channels: ch, bitDepth: bitDepth)
            }
            print("Saved: \(finalURL.path)")
        } catch {
            fputs("Error writing file: \(error)\n", stderr)
            return 1
        }
        return 0
    }
}

// MARK: - Entry point

let args = CommandLine.arguments
guard args.count == 4 else {
    fputs("Usage: recorder <output.wav> <duration_hint_seconds> <device_name>\n", stderr)
    fputs("       duration_hint: approximate recording length in seconds\n", stderr)
    fputs("       device_name: substring match against input device name\n\n", stderr)
    listDevices()
    exit(1)
}

let outputPath = args[1]
guard let durationHint = Double(args[2]), durationHint > 0 else {
    fputs("Error: duration_hint must be a positive number of seconds\n", stderr)
    exit(1)
}
let deviceName = args[3]

let outputURL = URL(fileURLWithPath: outputPath)

// Ensure output directory exists
let dir = outputURL.deletingLastPathComponent()
if !FileManager.default.fileExists(atPath: dir.path) {
    fputs("Error: output directory does not exist: \(dir.path)\n", stderr)
    exit(1)
}

let recorder = Recorder(outputURL: outputURL, durationHint: durationHint, deviceName: deviceName)
let result = recorder.run()
exit(result)
