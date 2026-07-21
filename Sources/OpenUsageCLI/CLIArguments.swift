import Foundation

struct CLIArguments: Equatable, Sendable {
    var providerID: String?
    var force = false
    var showHelp = false
    var showVersion = false
    /// Emit the richer `/v1/usage` UI payload (every metric line per provider) instead of the stable
    /// `/v1/limits` contract. Used by the Windows system-tray UI to render the full popover.
    var uiPayload = false

    static func parse(_ arguments: [String]) throws -> CLIArguments {
        var parsed = CLIArguments()
        for argument in arguments {
            switch argument {
            case "--force": parsed.force = true
            case "--ui": parsed.uiPayload = true
            case "-h", "--help": parsed.showHelp = true
            case "-v", "--version": parsed.showVersion = true
            default:
                if argument.hasPrefix("-") {
                    throw CLIError.usage("Unknown option: \(argument)")
                }
                guard parsed.providerID == nil else {
                    throw CLIError.usage("Only one provider can be requested at a time.")
                }
                parsed.providerID = argument.lowercased()
            }
        }
        return parsed
    }
}

enum CLIError: Error, Equatable {
    case usage(String)
    case appDefaultsUnavailable
}
