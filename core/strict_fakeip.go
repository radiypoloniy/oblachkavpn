package main

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"net/netip"
	"strings"
	"sync"
	"time"

	mdns "github.com/miekg/dns"
	"github.com/sagernet/bbolt"
	"github.com/sagernet/sing-box/adapter"
	C "github.com/sagernet/sing-box/constant"
	"github.com/sagernet/sing-box/dns"
	"github.com/sagernet/sing-box/dns/transport/fakeip"
	"github.com/sagernet/sing-box/log"
	"github.com/sagernet/sing-box/option"
)

type strictFakeIPOptions struct {
	option.FakeIPDNSServerOptions
	StatePath string `json:"state_path,omitempty"`
}

func registerStrictFakeIP(registry *dns.TransportRegistry) {
	dns.RegisterTransport(registry, C.DNSTypeFakeIP, newStrictFakeIP)
}

type strictFakeIP struct {
	dns.TransportAdapter
	store strictFakeIPStore
	ipv6  bool
}

func newStrictFakeIP(ctx context.Context, logger log.ContextLogger, tag string, opts strictFakeIPOptions) (adapter.DNSTransport, error) {
	if opts.StatePath == "" {
		return fakeip.NewTransport(ctx, logger, tag, opts.FakeIPDNSServerOptions)
	}
	return &strictFakeIP{
		TransportAdapter: dns.NewTransportAdapter(C.DNSTypeFakeIP, tag, nil),
		store:            strictFakeIPStore{path: opts.StatePath},
		ipv6:             opts.Inet6Range != nil,
	}, nil
}

func (t *strictFakeIP) Start(stage adapter.StartStage, scope *adapter.Scope) error {
	if stage != adapter.StartStateStart {
		return nil
	}
	if err := t.store.Start(); err != nil {
		return err
	}
	scope.Add(t.store.Close)
	return nil
}

func (t *strictFakeIP) Reset() {}

func (t *strictFakeIP) Store() adapter.FakeIPStore { return &t.store }

func (t *strictFakeIP) Exchange(_ context.Context, message *mdns.Msg) (*mdns.Msg, error) {
	question := message.Question[0]
	switch question.Qtype {
	case mdns.TypeA:
	case mdns.TypeAAAA:
		if !t.ipv6 {
			return dns.FixedResponseStatus(message, mdns.RcodeSuccess), nil
		}
	default:
		return nil, errors.New("only address queries are supported by FakeIP")
	}
	address, err := t.store.Create(dns.FqdnToDomain(question.Name), question.Qtype == mdns.TypeAAAA)
	if err != nil {
		return nil, err
	}
	return dns.FixedResponse(message.Id, question, []netip.Addr{address}, 0), nil
}

func (t *strictFakeIP) ExchangeAsync(ctx context.Context, message *mdns.Msg, callback func(*mdns.Msg, error)) {
	callback(t.Exchange(ctx, message))
}

var (
	strictIPv4     = netip.MustParsePrefix("198.18.0.0/15")
	strictIPv6     = netip.MustParsePrefix("2001:2::/48")
	strictIPv6Pool = netip.MustParsePrefix("2001:2::/111")
)

// Commit the domain digest and allocation cursor together before publishing an IP.
// Only the live process keeps the reverse map with cleartext names. An address
// from a prior process is rejected until the same name is queried again.
type strictFakeIPStore struct {
	path  string
	db    *bbolt.DB
	names sync.Map
}

func (s *strictFakeIPStore) Start() error {
	db, err := bbolt.Open(s.path, 0600, &bbolt.Options{Timeout: 2 * time.Second})
	if err != nil {
		return fmt.Errorf("open strict DNS state: %w", err)
	}
	s.db = db
	return nil
}

func (s *strictFakeIPStore) Close() error { return s.db.Close() }

func (s *strictFakeIPStore) Contains(address netip.Addr) bool {
	return strictIPv4.Contains(address) || strictIPv6.Contains(address)
}

func (s *strictFakeIPStore) Lookup(address netip.Addr) (string, bool) {
	domain, ok := s.names.Load(address)
	if !ok {
		return "", false
	}
	return domain.(string), true
}

func (s *strictFakeIPStore) Reset() error {
	s.names.Clear()
	return nil
}

func (s *strictFakeIPStore) Create(domain string, ipv6 bool) (netip.Addr, error) {
	domain = strings.ToLower(domain)
	digest := sha256.Sum256([]byte(domain))
	prefix, family := strictIPv4, "ipv4"
	if ipv6 {
		prefix, family = strictIPv6Pool, "ipv6"
	}
	var address netip.Addr
	cursor := []byte("last")
	err := s.db.Update(func(tx *bbolt.Tx) error {
		bucket, err := tx.CreateBucketIfNotExists([]byte(family))
		if err != nil {
			return err
		}
		if saved := bucket.Get(digest[:]); saved != nil {
			var valid bool
			address, valid = netip.AddrFromSlice(saved)
			if !valid || !prefix.Contains(address) {
				return errors.New("invalid strict DNS address")
			}
			return nil
		}
		last := prefix.Addr().Next()
		if saved := bucket.Get(cursor); saved != nil {
			var valid bool
			last, valid = netip.AddrFromSlice(saved)
			if !valid || !prefix.Contains(last) {
				return errors.New("invalid strict DNS cursor")
			}
		}
		address = last.Next()
		if !prefix.Contains(address.Next()) {
			return errors.New("strict DNS address pool exhausted")
		}
		if err := bucket.Put(digest[:], address.AsSlice()); err != nil {
			return err
		}
		return bucket.Put(cursor, address.AsSlice())
	})
	if err != nil {
		return netip.Addr{}, err
	}
	s.names.Store(address, domain)
	return address, nil
}
