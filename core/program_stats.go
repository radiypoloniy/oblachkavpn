package main

import (
	"slices"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"

	"github.com/gofrs/uuid/v5"
	"github.com/sagernet/sing-box/common/trafficcontrol"
	C "github.com/sagernet/sing-box/constant"
)

const maxStatPrograms = 256
const otherStatPrograms = "Другие программы"

type programCounters struct {
	Path     string `json:"path"`
	Upload   int64  `json:"upload"`
	Download int64  `json:"download"`
	VPN      int    `json:"vpn"`
	Direct   int    `json:"direct"`
	UDP      int    `json:"udp"`
	LastSeen int64  `json:"lastSeen"`
}

type programSnapshot struct {
	Version  int               `json:"version"`
	Epoch    string            `json:"epoch"`
	Programs []programCounters `json:"programs"`
}

type archivedCounters struct{ upload, download int64 }
type observedCounters struct {
	path string
	archivedCounters
}
type programState struct {
	programCounters
	closedUpload, closedDownload int64
}

// Uses existing byte counters: no extra wrappers or atomics in the packet path.
// Closed metadata is the bounded (1000 entries) upstream traffic-manager window.
type programStats struct {
	mu          sync.Mutex
	epoch       string
	programs    map[string]*programState
	closed      map[uuid.UUID]archivedCounters
	active      map[uuid.UUID]observedCounters
	spareClosed map[uuid.UUID]archivedCounters
	spareActive map[uuid.UUID]observedCounters
	totals      map[*programState]archivedCounters
}

func newProgramStats() *programStats {
	id, _ := uuid.NewV4()
	return &programStats{epoch: id.String(), programs: make(map[string]*programState), closed: make(map[uuid.UUID]archivedCounters), active: make(map[uuid.UUID]observedCounters)}
}

func (s *programStats) program(path string) *programState {
	if len(path) > 1024 || strings.IndexFunc(path, unicode.IsControl) >= 0 {
		path = otherStatPrograms
	}
	key := strings.ToLower(path)
	if known := s.programs[key]; known != nil {
		return known
	}
	if len(s.programs) >= maxStatPrograms-1 {
		key, path = strings.ToLower(otherStatPrograms), otherStatPrograms
		if known := s.programs[key]; known != nil {
			return known
		}
	}
	p := &programState{programCounters: programCounters{Path: path}}
	s.programs[key] = p
	return p
}

func statsProcessPath(c *trafficcontrol.TrackerMetadata) string {
	info := c.Metadata.ProcessInfo
	if info == nil {
		return ""
	}
	path := info.ProcessPath
	if path == "" && len(info.AndroidPackageNames) > 0 {
		path = info.AndroidPackageNames[0]
	}
	if path == "" {
		if info.UserId != -1 {
			return strconv.Itoa(int(info.UserId))
		}
		return ""
	}
	if info.UserName != "" {
		return path + " (" + info.UserName + ")"
	}
	if info.UserId != -1 {
		return path + " (" + strconv.Itoa(int(info.UserId)) + ")"
	}
	return path
}

func countProgram(p *programState, c *trafficcontrol.TrackerMetadata) {
	if slices.Contains(c.Chain, "user-direct") {
		p.Direct++
	} else {
		p.VPN++
	}
	if c.Metadata.Network == "udp" {
		p.UDP++
	}
}

func (s *programStats) snapshot(active, closed []*trafficcontrol.TrackerMetadata, now time.Time) programSnapshot {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, p := range s.programs {
		p.VPN, p.Direct, p.UDP = 0, 0, 0
	}
	seenClosed := s.spareClosed
	if seenClosed == nil {
		seenClosed = make(map[uuid.UUID]archivedCounters, len(closed))
	}
	clear(seenClosed)
	for _, c := range closed {
		path := statsProcessPath(c)
		if path == "" || c.OutboundType == C.TypeDNS {
			continue
		}
		p := s.program(path)
		u, d := c.Upload.Load(), c.Download.Load()
		old, known := s.closed[c.ID]
		p.closedUpload += max(0, u-old.upload)
		p.closedDownload += max(0, d-old.download)
		seenClosed[c.ID] = archivedCounters{u, d}
		p.LastSeen = max(p.LastSeen, c.ClosedAt.UnixMilli())
		if !known {
			countProgram(p, c)
		}
	}
	if s.totals == nil {
		s.totals = make(map[*programState]archivedCounters, maxStatPrograms)
	}
	totals := s.totals
	clear(totals)
	seenActive := s.spareActive
	if seenActive == nil {
		seenActive = make(map[uuid.UUID]observedCounters, len(active))
	}
	clear(seenActive)
	for _, c := range active {
		if _, closed := seenClosed[c.ID]; closed {
			continue
		}
		path := statsProcessPath(c)
		if path == "" || c.OutboundType == C.TypeDNS {
			continue
		}
		p := s.program(path)
		total := totals[p]
		u, d := max(0, c.Upload.Load()), max(0, c.Download.Load())
		total.upload += u
		total.download += d
		totals[p] = total
		seenActive[c.ID] = observedCounters{path, archivedCounters{u, d}}
		p.LastSeen = now.UnixMilli()
		countProgram(p, c)
	}
	for id, old := range s.active {
		_, active := seenActive[id]
		_, closed := seenClosed[id]
		_, archived := s.closed[id]
		if !active && !closed && !archived {
			// Already observed connection evicted from upstream's closed window:
			// preserve at least its observed bytes, as the legacy client did.
			p := s.program(old.path)
			p.closedUpload += old.upload
			p.closedDownload += old.download
		}
	}
	// Double-buffer only bounded scratch maps. Never retain a large connection spike.
	s.spareActive, s.spareClosed = nil, nil
	if len(s.active) <= 4096 {
		clear(s.active)
		s.spareActive = s.active
	}
	if len(s.closed) <= 4096 {
		clear(s.closed)
		s.spareClosed = s.closed
	}
	s.active, s.closed = seenActive, seenClosed
	// Result storage must remain owned by this call: HTTP encoding happens after unlocking.
	result := programSnapshot{Version: 1, Epoch: s.epoch, Programs: make([]programCounters, 0, len(s.programs))}
	for _, p := range s.programs {
		// A close racing the two upstream snapshots may temporarily hide a connection.
		// Keep counters monotonic until the next poll observes its closed metadata.
		p.Upload = max(p.Upload, p.closedUpload+totals[p].upload)
		p.Download = max(p.Download, p.closedDownload+totals[p].download)
		result.Programs = append(result.Programs, p.programCounters)
	}
	return result
}
