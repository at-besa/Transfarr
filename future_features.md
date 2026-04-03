# Transfarr - Future Features List

This document keeps track of planned and proposed features for future development phases.

## 1. Download Resuming
- Implement HTTP-Range-Requests (or an equivalent mechanism in our P2P protocol) to resume interrupted downloads at the exact point they stopped.

## 2. TTH Swarm Fetching (Queue Matching)
- Utilize the generated TTH hashes to find identical files hosted by different users.
- Allow parallel downloading of the same file from multiple peers simultaneously (similar to the BitTorrent swarm principle) to maximize download speed and increase reliability.

## 3. Desktop Application & System-Tray Integration
- Evaluate and integrate a desktop framework (e.g., **MAUI Blazor Hybrid**, **Photino**, or **WPF/WebView2**) to run the Node application natively on the desktop.
- Implement **System-Tray logic** (minimize to tray, background service functionality).
- Create a professional **Installer** (e.g., InnoSetup or WiX Toolset) for easy distribution to end-users.

## 4. Real-Time Dashboard & Global Speed Control
- Create a modern, visual dashboard in the UI.
- Display graphs for current upload/download speeds, connection quality, and overall bandwidth utilization.
- Add a persistent global speed indicator (Upload/Download rates) to the application's bottom bar.
- Implement **Speed Limiting**, allowing users to set a maximum allowed upload and download bandwidth limit in the UI, enforcing it via throttling in the Core transfers.

## 5. Share-List Indexing (Search Optimization)
- Optimize the local database (ShareDatabase) with full-text search and indexing.
- Enable lightning-fast search queries, even when users are sharing tens of thousands of files.

## 6. Reputation System Expansion
- Expand the rudimentary point system (10 GB = 1 point).
- Introduce user ranks (e.g., Bronze, Silver, Gold) or privileges (e.g., higher download priority from peers you have uploaded heavily to).
