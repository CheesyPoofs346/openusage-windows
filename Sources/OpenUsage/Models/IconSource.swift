/// A provider's copied vector mark, keyed by provider id.
///
/// The pure model half of the icon system — kept in `Models` (never platform-excluded) so provider
/// and widget models compile everywhere. The SwiftUI rendering (`ProviderIcon`, `ProviderIconShape`)
/// lives in `Support/ProviderIconShape.swift`, which the Windows CLI build excludes.
struct IconSource: Hashable {
    let providerID: String

    /// Named constructor retained at call sites so the stored string's meaning stays explicit.
    static func providerMark(_ providerID: String) -> IconSource {
        IconSource(providerID: providerID)
    }
}
