package main

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"runtime"
	"sync"
	"time"

	"github.com/sagernet/sing-box/adapter"
	boxService "github.com/sagernet/sing-box/adapter/service"
	"github.com/sagernet/sing-box/common/trafficcontrol"
	"github.com/sagernet/sing-box/log"
	"github.com/sagernet/sing/service"
)

const programServiceType = "oblachka-programs"

type programServiceOptions struct {
	Listen string `json:"listen"`
	Secret string `json:"secret"`
}

type programService struct {
	tag      string
	logger   log.ContextLogger
	manager  *trafficcontrol.Manager
	stats    *programStats
	options  programServiceOptions
	sampleMu sync.Mutex
}

func registerProgramService(registry *boxService.Registry) {
	boxService.Register(registry, programServiceType, newProgramService)
}

func newProgramService(ctx context.Context, logger log.ContextLogger, tag string, options programServiceOptions) (adapter.Service, error) {
	address, err := net.ResolveTCPAddr("tcp4", options.Listen)
	if err != nil || !address.IP.Equal(net.IPv4(127, 0, 0, 1)) || address.Port < 1024 || address.Port > 65535 || len(options.Secret) < 16 || len(options.Secret) > 64 {
		return nil, fmt.Errorf("invalid local program statistics settings")
	}
	manager := service.PtrFromContext[trafficcontrol.Manager](ctx)
	if manager == nil {
		return nil, fmt.Errorf("program statistics requires traffic manager")
	}
	return &programService{tag: tag, logger: logger, manager: manager, stats: newProgramStats(), options: options}, nil
}

func (s *programService) Type() string { return programServiceType }
func (s *programService) Tag() string  { return s.tag }

func (s *programService) Start(stage adapter.StartStage, scope *adapter.Scope) error {
	if stage != adapter.StartStateStarted {
		return nil
	}
	listener, err := net.Listen("tcp4", s.options.Listen)
	if err != nil {
		// Optional optimisation: its port becoming busy must not break the tunnel.
		s.logger.Warn("program statistics unavailable")
		return nil
	}
	server := &http.Server{Handler: http.HandlerFunc(s.serve), ReadHeaderTimeout: 2 * time.Second, WriteTimeout: 2 * time.Second, IdleTimeout: 15 * time.Second, MaxHeaderBytes: 8192}
	scope.Add(server.Close)
	go server.Serve(listener)
	return nil
}

func (s *programService) serve(w http.ResponseWriter, r *http.Request) {
	if subtle.ConstantTimeCompare([]byte(r.Header.Get("Authorization")), []byte("Bearer "+s.options.Secret)) != 1 {
		w.WriteHeader(http.StatusUnauthorized)
		return
	}
	if r.Method != http.MethodGet || (r.URL.Path != "/programs" && r.URL.Path != "/runtime") {
		w.WriteHeader(http.StatusNotFound)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	if r.URL.Path == "/runtime" {
		var m runtime.MemStats
		runtime.ReadMemStats(&m)
		json.NewEncoder(w).Encode(map[string]any{"heapAlloc": m.HeapAlloc, "heapInuse": m.HeapInuse, "heapSys": m.HeapSys, "stackInuse": m.StackInuse, "totalAlloc": m.TotalAlloc, "numGC": m.NumGC, "gcPauseNs": m.PauseTotalNs, "goroutines": runtime.NumGoroutine()})
		return
	}
	// Read active first; de-duplicate connections also present in the closed window.
	s.sampleMu.Lock()
	active := s.manager.Connections()
	closed := s.manager.ClosedConnections()
	snapshot := s.stats.snapshot(active, closed, time.Now())
	s.sampleMu.Unlock()
	json.NewEncoder(w).Encode(snapshot)
}
