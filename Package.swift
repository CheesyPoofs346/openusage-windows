// swift-tools-version: 6.2
import PackageDescription

#if os(Windows)
// Windows edition: the SwiftUI/AppKit menu-bar app cannot run here, so we build only the
// `openusage-cli` usage reader on top of a trimmed core. All SwiftUI/AppKit/Sparkle/KeyboardShortcuts/
// PostHog files are excluded; CryptoKit is supplied by swift-crypto (drop-in `Crypto` module).
let package = Package(
    name: "OpenUsage",
    products: [
        .executable(name: "openusage-cli", targets: ["OpenUsageCLI"])
    ],
    dependencies: [
        // CryptoKit substitute on non-Apple platforms — same API surface (SHA256, AES.GCM, …).
        .package(url: "https://github.com/apple/swift-crypto.git", from: "3.0.0")
    ],
    targets: [
        .target(
            name: "OpenUsage",
            dependencies: [
                .product(name: "Crypto", package: "swift-crypto")
            ],
            path: "Sources/OpenUsage",
            exclude: [
                // Whole GUI subsystems — no equivalent on Windows.
                "Views",
                "App",
                // Apple-only services the CLI never invokes.
                "Services/ScreenCaptureProbe.swift",
                "Services/LocalUsageServer.swift",
                "Services/CommandLineToolInstaller.swift",
                // SwiftUI/AppKit-importing files outside Views/App.
                "Providers/Codex/CodexResetClaimService.swift",
                "Stores/LaunchAtLoginSetting.swift",
                "Stores/AppearanceSetting.swift",
                "Stores/DensitySetting.swift",
                "Stores/LayoutStore.swift",
                "Stores/LayoutStore+Customization.swift",
                "Stores/ICloudUsageSyncStore.swift",
                "Stores/MenuBarPrivacyStore.swift",
                "Stores/PopoverTransparencyStore.swift",
                "Stores/PopoverTransparencyStyle.swift",
                "Support/AboutPanel.swift",
                "Support/Animations.swift",
                "Support/AppNotifications.swift",
                "Support/AppShortcuts.swift",
                "Support/Haptics.swift",
                "Support/InvisibleOverlayScroller.swift",
                "Support/LiquidGlassFallbacks.swift",
                "Support/MenuBarIcon.swift",
                "Support/MenuBarStripRenderer.swift",
                "Support/PartyMode.swift",
                "Support/PopoverDismissReader.swift",
                "Support/PopoverSurfaceTreatment.swift",
                "Support/ProviderIconShape.swift",
                "Support/ShareCardRenderer.swift",
                "Support/Theme.swift",
                "Support/TooMuchTransparencyEffect.swift",
                "Support/TooMuchTransparencyKeyReader.swift"
            ],
            resources: [
                .copy("Resources/ProviderIcons"),
                .copy("Resources/pricing_supplement.json"),
                .copy("Resources/pricing_litellm_snapshot.json"),
                .copy("Resources/pricing_models_dev_snapshot.json")
            ],
            swiftSettings: [
                .swiftLanguageMode(.v6)
            ]
        ),
        .executableTarget(
            name: "OpenUsageCLI",
            dependencies: ["OpenUsage"],
            path: "Sources/OpenUsageCLI",
            swiftSettings: [
                .swiftLanguageMode(.v6)
            ]
        )
    ]
)
#else
let package = Package(
    name: "OpenUsage",
    platforms: [
        .macOS(.v15)
    ],
    products: [
        .executable(name: "OpenUsage", targets: ["OpenUsageApp"]),
        .executable(name: "openusage-cli", targets: ["OpenUsageCLI"])
    ],
    dependencies: [
        // The de-facto standard recorder + global hotkey for Mac apps (System Settings-style field).
        .package(url: "https://github.com/sindresorhus/KeyboardShortcuts", from: "3.0.1"),
        // In-app auto-updates (appcast + EdDSA-signed downloads). 2.9.4 fixes the update window opening
        // behind other apps for menu-bar (dockless) apps (sparkle-project/Sparkle#2889).
        .package(url: "https://github.com/sparkle-project/Sparkle", from: "2.9.4"),
        // Anonymous, opt-out product analytics (official, MIT-licensed, first-party Swift SDK).
        .package(url: "https://github.com/PostHog/posthog-ios.git", from: "3.62.0")
    ],
    targets: [
        .target(
            name: "OpenUsage",
            dependencies: [
                .product(name: "KeyboardShortcuts", package: "KeyboardShortcuts"),
                .product(name: "Sparkle", package: "Sparkle"),
                .product(name: "PostHog", package: "posthog-ios")
            ],
            path: "Sources/OpenUsage",
            resources: [
                .copy("Resources/ProviderIcons"),
                .copy("Resources/pricing_supplement.json"),
                .copy("Resources/pricing_litellm_snapshot.json"),
                .copy("Resources/pricing_models_dev_snapshot.json")
            ],
            swiftSettings: [
                .swiftLanguageMode(.v6)
            ]
        ),
        .executableTarget(
            name: "OpenUsageApp",
            dependencies: ["OpenUsage"],
            path: "Sources/OpenUsageApp",
            swiftSettings: [
                .swiftLanguageMode(.v6)
            ]
        ),
        .executableTarget(
            name: "OpenUsageCLI",
            dependencies: ["OpenUsage"],
            path: "Sources/OpenUsageCLI",
            swiftSettings: [
                .swiftLanguageMode(.v6)
            ]
        ),
        .testTarget(
            name: "OpenUsageTests",
            dependencies: ["OpenUsage"],
            path: "Tests/OpenUsageTests",
            swiftSettings: [
                .swiftLanguageMode(.v6)
            ]
        ),
        .testTarget(
            name: "OpenUsageCLITests",
            dependencies: ["OpenUsageCLI"],
            path: "Tests/OpenUsageCLITests",
            swiftSettings: [
                .swiftLanguageMode(.v6)
            ]
        )
    ]
)
#endif
