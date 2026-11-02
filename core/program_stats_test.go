package main

import (
	"encoding/json"
	"fmt"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/gofrs/uuid/v5"
	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/common/trafficcontrol"
)

func fixtureConnection(path string, up, down int64) *trafficcontrol.TrackerMetadata {
	id, _ := uuid.NewV4()
	u, d := new(atomic.Int64), new(atomic.Int64)
	u.Store(up)
	d.Store(down)
	return &trafficcontrol.TrackerMetadata{ID: id, Upload: u, Download: d, Chain: []string{"server-selector", "direct-0"}, OutboundType: "vless", CreatedAt: time.Now(),
		Metadata: adapter.InboundContext{Network: "tcp", ProcessInfo: &adapter.ConnectionOwner{ProcessPath: path, UserId: -1}}}
}

func TestProgramStatsClosingAndCumulativeCounters(t *testing.T) {
	s := newProgramStats()
	now := time.Now()
	c := fixtureConnection(`C:\Apps\browser.exe`, 10, 20)
	first := s.snapshot([]*trafficcontrol.TrackerMetadata{c}, nil, now)
	if first.Version != 1 || first.Programs[0].Upload != 10 || first.Programs[0].VPN != 1 {
		t.Fatal(first)
	}
	c.Upload.Store(30)
	c.Download.Store(40)
	c.ClosedAt = now
	closed := s.snapshot([]*trafficcontrol.TrackerMetadata{c}, []*trafficcontrol.TrackerMetadata{c}, now)
	if closed.Programs[0].Upload != 30 || closed.Programs[0].Download != 40 {
		t.Fatal("closing counted twice", closed)
	}
	again := s.snapshot(nil, []*trafficcontrol.TrackerMetadata{c}, now.Add(time.Second))
	if again.Programs[0].Upload != 30 || again.Programs[0].VPN != 0 || again.Programs[0].LastSeen != now.UnixMilli() {
		t.Fatal(again)
	}
	c.Upload.Store(35)
	if got := s.snapshot(nil, []*trafficcontrol.TrackerMetadata{c}, now).Programs[0].Upload; got != 35 {
		t.Fatal(got)
	}
	next := fixtureConnection(`c:\apps\BROWSER.exe`, 5, 7)
	next.Chain = []string{"user-direct"}
	next.Metadata.Network = "udp"
	last := s.snapshot([]*trafficcontrol.TrackerMetadata{next}, nil, now.Add(time.Second))
	if len(last.Programs) != 1 || last.Programs[0].Upload != 40 || last.Programs[0].Direct != 1 || last.Programs[0].UDP != 1 {
		t.Fatal(last)
	}
}

func TestProgramStatsShortConnectionsOverflowAndEviction(t *testing.T) {
	s := newProgramStats()
	now := time.Now()
	var closed []*trafficcontrol.TrackerMetadata
	for i := 0; i < 1000; i++ {
		c := fixtureConnection(fmt.Sprintf(`C:\Apps\%d.exe`, i), 1, 2)
		c.ClosedAt = now
		closed = append(closed, c)
	}
	got := s.snapshot(nil, closed, now)
	var up, down int64
	for _, p := range got.Programs {
		up += p.Upload
		down += p.Download
	}
	if len(got.Programs) != maxStatPrograms || up != 1000 || down != 2000 {
		t.Fatal(len(got.Programs), up, down)
	}
	if len(s.snapshot(nil, nil, now).Programs) != maxStatPrograms || len(s.closed) != 0 {
		t.Fatal("unbounded closed cache")
	}
	// A previously observed connection can leave the upstream closed window
	// between polls. Its observed bytes must survive without stalling future ones.
	s = newProgramStats()
	c := fixtureConnection("browser", 100, 200)
	s.snapshot([]*trafficcontrol.TrackerMetadata{c}, nil, now)
	s.snapshot(nil, nil, now)
	next := fixtureConnection("browser", 1, 2)
	if got := s.snapshot([]*trafficcontrol.TrackerMetadata{next}, nil, now).Programs[0]; got.Upload != 101 || got.Download != 202 {
		t.Fatal(got)
	}
}

func TestProgramStatsBoundsUnrepresentablePaths(t *testing.T) {
	s := newProgramStats()
	got := s.snapshot([]*trafficcontrol.TrackerMetadata{fixtureConnection(strings.Repeat("x", 1025), 10, 20), fixtureConnection("invalid\npath", 1, 2)}, nil, time.Now())
	if len(got.Programs) != 1 || got.Programs[0].Path != otherStatPrograms || got.Programs[0].Upload != 11 || got.Programs[0].Download != 22 {
		t.Fatal(got)
	}
}

func TestProgramStatsSnapshotOwnershipAndScratchBound(t *testing.T) {
	s := newProgramStats()
	now := time.Now()
	c := fixtureConnection("browser", 10, 20)
	first := s.snapshot([]*trafficcontrol.TrackerMetadata{c}, nil, now)
	for i := 0; i < 5; i++ {
		c.Upload.Add(1)
		s.snapshot([]*trafficcontrol.TrackerMetadata{c}, nil, now)
	}
	if first.Programs[0].Upload != 10 || first.Programs[0].Download != 20 {
		t.Fatal("scratch reuse mutated an earlier HTTP response", first)
	}
	spike := make([]*trafficcontrol.TrackerMetadata, 4097)
	for i := range spike {
		spike[i] = fixtureConnection("browser", 1, 2)
	}
	s.snapshot(spike, nil, now)
	s.snapshot(nil, nil, now)
	if s.spareActive != nil {
		t.Fatal("retained active scratch after a large connection spike")
	}
	for _, c := range spike {
		c.ClosedAt = now
	}
	s.snapshot(nil, spike, now)
	s.snapshot(nil, nil, now)
	if s.spareClosed != nil {
		t.Fatal("retained closed scratch after a large connection spike")
	}
}

// Same JSON shape as the upstream Clash /connections formatter, including
// nested per-connection maps. No TUN or external server is used by this benchmark.
type legacyProgramConnection struct {
	c *trafficcontrol.TrackerMetadata
}

func (v legacyProgramConnection) MarshalJSON() ([]byte, error) {
	c := v.c
	return json.Marshal(map[string]any{
		"id": c.ID, "upload": c.Upload.Load(), "download": c.Download.Load(), "start": c.CreatedAt, "chains": c.Chain, "rule": "final", "rulePayload": "",
		"metadata": map[string]any{"network": c.Metadata.Network, "type": "tun/system-tun", "sourceIP": "172.31.255.1", "destinationIP": "192.0.2.1", "sourcePort": "50000", "destinationPort": "443", "host": "site.fixture.invalid", "dnsMode": "normal", "processPath": statsProcessPath(c)},
	})
}

func BenchmarkProgramPoll(b *testing.B) {
	active := make([]*trafficcontrol.TrackerMetadata, 2000)
	legacy := make([]legacyProgramConnection, len(active))
	for i := range active {
		active[i] = fixtureConnection(fmt.Sprintf(`C:\Apps\program-%d.exe`, i%100), 100, 1000)
		legacy[i] = legacyProgramConnection{active[i]}
	}
	b.Run("FullConnections", func(b *testing.B) {
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			if _, err := json.Marshal(map[string]any{"connections": legacy, "uploadTotal": 200000, "downloadTotal": 2000000, "memory": 0}); err != nil {
				b.Fatal(err)
			}
		}
	})
	b.Run("CompactPrograms", func(b *testing.B) {
		s := newProgramStats()
		now := time.Now()
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			if _, err := json.Marshal(s.snapshot(active, nil, now)); err != nil {
				b.Fatal(err)
			}
		}
	})
}

func BenchmarkOneProgram500(b *testing.B) {
	active := make([]*trafficcontrol.TrackerMetadata, 500)
	for i := range active {
		active[i] = fixtureConnection(`C:\Apps\fixture.exe`, 100, 1000)
	}
	s := newProgramStats()
	now := time.Now()
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		if _, err := json.Marshal(s.snapshot(active, nil, now)); err != nil {
			b.Fatal(err)
		}
	}
}
