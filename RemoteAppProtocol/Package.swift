// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "RemoteAppProtocol",
    platforms: [.macOS(.v14), .iOS(.v17)],
    products: [
        .library(name: "RemoteAppProtocol", targets: ["RemoteAppProtocol"]),
    ],
    targets: [
        .target(name: "RemoteAppProtocol"),
        .testTarget(name: "RemoteAppProtocolTests", dependencies: ["RemoteAppProtocol"]),
    ],
    swiftLanguageVersions: [.v5]
)
