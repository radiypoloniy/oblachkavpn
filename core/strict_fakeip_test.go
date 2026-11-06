package main

import (
	"net/netip"
	"path/filepath"
	"testing"

	"github.com/sagernet/bbolt"
)

func TestStrictFakeIPRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "strict-dns.db")
	first := &strictFakeIPStore{path: path}
	if err := first.Start(); err != nil {
		t.Fatal(err)
	}
	v4, err := first.Create("old.invalid", false)
	if err != nil {
		t.Fatal(err)
	}
	v6, err := first.Create("old.invalid", true)
	if err != nil {
		t.Fatal(err)
	}
	if err := first.Close(); err != nil {
		t.Fatal(err)
	}
	second := &strictFakeIPStore{path: path}
	if err := second.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = second.Close() })
	for _, old := range []netip.Addr{v4, v6} {
		if _, found := second.Lookup(old); found || !second.Contains(old) {
			t.Fatalf("old address %s must be intercepted and rejected before renewal", old)
		}
		fresh, err := second.Create("different.invalid", old.Is6())
		if err != nil || fresh == old {
			t.Fatalf("allocated %s, error %v, old address %s", fresh, err, old)
		}
		renewed, err := second.Create("OLD.INVALID", old.Is6())
		if err != nil || renewed != old {
			t.Fatalf("renewed %s, error %v, old address %s", renewed, err, old)
		}
		if domain, ok := second.Lookup(old); !ok || domain != "old.invalid" {
			t.Fatalf("old address resolved to %q", domain)
		}
	}
}

func TestStrictFakeIPExhaustion(t *testing.T) {
	store := &strictFakeIPStore{path: filepath.Join(t.TempDir(), "strict-dns.db")}
	if err := store.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = store.Close() })
	for _, family := range []struct {
		bucket, last string
		ipv6         bool
	}{
		{"ipv4", "198.19.255.254", false},
		{"ipv6", "2001:2::1:fffe", true},
	} {
		if _, err := store.Create("first.invalid", family.ipv6); err != nil {
			t.Fatal(err)
		}
		err := store.db.Update(func(tx *bbolt.Tx) error {
			return tx.Bucket([]byte(family.bucket)).Put([]byte("last"), netip.MustParseAddr(family.last).AsSlice())
		})
		if err != nil {
			t.Fatal(err)
		}
		if _, err := store.Create("new.invalid", family.ipv6); err == nil {
			t.Fatalf("the %s pool must not wrap", family.bucket)
		}
		if _, err := store.Create("first.invalid", family.ipv6); err != nil {
			t.Fatal(err)
		}
	}
}
