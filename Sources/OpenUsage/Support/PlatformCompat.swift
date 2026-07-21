import Foundation

// Cross-platform stand-ins for Apple-only primitives, active only where the `os` module is absent
// (the Windows CLI build). On Apple platforms the real types are used and this file adds nothing.
#if !canImport(os)

/// Minimal replacement for `os.OSAllocatedUnfairLock<State>`, backed by `NSLock`. Same surface the
/// codebase uses: `init(initialState:)` and `withLock { (inout State) -> R }`.
final class OSAllocatedUnfairLock<State>: @unchecked Sendable {
    private let lock = NSLock()
    private var state: State

    init(initialState: State) { self.state = initialState }
    init() where State == Void { self.state = () }

    @discardableResult
    func withLock<R>(_ body: (inout State) throws -> R) rethrows -> R {
        lock.lock()
        defer { lock.unlock() }
        return try body(&state)
    }

    @discardableResult
    func withLockUnchecked<R>(_ body: (inout State) throws -> R) rethrows -> R {
        try withLock(body)
    }
}

#endif
